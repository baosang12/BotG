using System;
using System.Collections.Generic;

namespace Analysis.Wyckoff
{
    public enum WyckoffPhase
    {
        None,
        PS,          // Preliminary Support/Supply
        SC_ACC,      // Selling Climax in Accumulation
        SC_DIST,     // Selling Climax in Distribution
        AR,          // Automatic Rally / Reaction
        ST,          // Secondary Test
        TRAP,        // Range Trap
        SOS_ACC,     // Sign of Strength in Accumulation
        SOS_DIST,    // Sign of Weakness in Distribution
        LPS_ACC,     // Last Point of Support
        LPS_DIST,    // Last Point of Supply
        Spring,      // Spring
        Upthrust,    // Upthrust
        Markup,      // Markup
        Markdown     // Markdown
    }

    public class WyckoffPhaseDetector
    {
        private readonly Action<string> _logger;
        public WyckoffPhase CurrentPhase { get; private set; } = WyckoffPhase.None;
        public DateTime? PhaseStartTime { get; private set; }
        public string PhaseNote { get; private set; }
        public List<(WyckoffPhase phase, DateTime time, string note)> PhaseHistory { get; } = new();

        /// <summary>
        /// Sự kiện được phát khi phase thay đổi: (oldPhase, newPhase, thời gian, ghi chú).
        /// </summary>
        public event Action<WyckoffPhase, WyckoffPhase, DateTime, string> OnPhaseChanged;

        public WyckoffPhaseDetector(Action<string> logger = null)
        {
            _logger = logger;
        }

        // Tổng hợp trạng thái phase
        public bool IsInAccumulation =>
            CurrentPhase == WyckoffPhase.PS ||
            CurrentPhase == WyckoffPhase.SC_ACC ||
            CurrentPhase == WyckoffPhase.AR ||
            CurrentPhase == WyckoffPhase.ST ||
            CurrentPhase == WyckoffPhase.TRAP;
        public bool IsReadyForBreakout =>
            CurrentPhase == WyckoffPhase.SOS_ACC ||
            CurrentPhase == WyckoffPhase.LPS_ACC ||
            CurrentPhase == WyckoffPhase.SOS_DIST ||
            CurrentPhase == WyckoffPhase.LPS_DIST;

        // Kiểm tra hợp lệ chuyển pha
        private static readonly HashSet<(WyckoffPhase from, WyckoffPhase to)> AllowedTransitions = new()
        {
            // Entry
            (WyckoffPhase.None, WyckoffPhase.PS),
            // Accumulation flow
            (WyckoffPhase.PS, WyckoffPhase.SC_ACC), (WyckoffPhase.SC_ACC, WyckoffPhase.AR),
            (WyckoffPhase.AR, WyckoffPhase.ST), (WyckoffPhase.ST, WyckoffPhase.TRAP),
            (WyckoffPhase.TRAP, WyckoffPhase.SOS_ACC), (WyckoffPhase.SOS_ACC, WyckoffPhase.LPS_ACC),
            (WyckoffPhase.LPS_ACC, WyckoffPhase.Spring), (WyckoffPhase.LPS_ACC, WyckoffPhase.Markup),
            (WyckoffPhase.Spring, WyckoffPhase.Markup),
            // Distribution flow
            (WyckoffPhase.PS, WyckoffPhase.SC_DIST), (WyckoffPhase.SC_DIST, WyckoffPhase.AR),
            (WyckoffPhase.AR, WyckoffPhase.ST), (WyckoffPhase.ST, WyckoffPhase.TRAP),
            (WyckoffPhase.TRAP, WyckoffPhase.Upthrust), (WyckoffPhase.Upthrust, WyckoffPhase.SOS_DIST),
            (WyckoffPhase.SOS_DIST, WyckoffPhase.LPS_DIST), (WyckoffPhase.LPS_DIST, WyckoffPhase.Markdown),
            // Spring transition
            (WyckoffPhase.TRAP, WyckoffPhase.Spring)
        };

        /// <summary>
        /// Xác định xem có thể chuyển từ phase hiện tại sang nextPhase.
        /// </summary>
        public bool CanTransitionTo(WyckoffPhase nextPhase)
            => nextPhase == WyckoffPhase.None || AllowedTransitions.Contains((CurrentPhase, nextPhase));

        private void ChangePhase(WyckoffPhase newPhase, DateTime time, string note)
        {
            if (CurrentPhase == newPhase)
            {
                _logger?.Invoke($"[WyckoffPhaseDetector] Phase unchanged: {newPhase} (already current phase)");
                return;
            }
            if (!CanTransitionTo(newPhase))
            {
                _logger?.Invoke($"[WyckoffPhaseDetector] Invalid phase transition: {CurrentPhase} -> {newPhase}");
                return;
            }
            var oldPhase = CurrentPhase;
            _logger?.Invoke($"[WyckoffPhaseDetector] Phase changed: {oldPhase} -> {newPhase} at {time:yyyy-MM-dd HH:mm}, note: {note}");
            CurrentPhase = newPhase;
            PhaseStartTime = time;
            PhaseNote = note;
            PhaseHistory.Add((newPhase, time, note));
            OnPhaseChanged?.Invoke(oldPhase, newPhase, time, note);
        }

        /// <summary>
        /// Cố gắng chuyển pha nếu thoả điều kiện.
        /// </summary>
        private void TryTransition<TEvent>(
            TEvent evt,
            WyckoffPhase requiredCurrent,
            WyckoffPhase targetPhase,
            Func<TEvent, bool> validate,
            Func<TEvent, DateTime> timeAccessor,
            Func<TEvent, string> noteAccessor
        )
        {
            if (evt == null || CurrentPhase != requiredCurrent || !validate(evt)) return;
            ChangePhase(targetPhase, timeAccessor(evt), noteAccessor(evt));
        }

        /// <summary>
        /// Xử lý sự kiện Climax, phân biệt Accumulation vs Distribution.
        /// </summary>
        public void UpdateWithClimax(ClimaxEvent ce)
        {
            TryTransition(
                ce,
                WyckoffPhase.None,
                ce.Type == ClimaxType.BuyingClimax ? WyckoffPhase.SC_ACC : WyckoffPhase.SC_DIST,
                e => true,
                e => e.Time,
                e => $"Climax {e.Type} tại {e.Time:yyyy-MM-dd HH:mm}"
            );
        }

        /// <summary>
        /// Cập nhật AR (Automatic Rally) sau SC_ACC hoặc SC_DIST.
        /// </summary>
        public void UpdateWithAR(AREvent ar)
        {
            if (ar == null || (CurrentPhase != WyckoffPhase.SC_ACC && CurrentPhase != WyckoffPhase.SC_DIST)) return;
            var prefix = CurrentPhase == WyckoffPhase.SC_ACC ? "[Accu]" : "[Distr]";
            ChangePhase(WyckoffPhase.AR, ar.Time, $"{prefix} AR tại {ar.Time:yyyy-MM-dd HH:mm}");
        }

        /// <summary>
        /// Cập nhật Range Trap chỉ sau ST.
        /// </summary>
        public void UpdateWithTrap(RangeTrapDetector rt)
        {
            TryTransition(
                rt,
                WyckoffPhase.ST,
                WyckoffPhase.TRAP,
                r => r.IsConfirmedTrap,
                r => r.ConfirmedTime ?? DateTime.MinValue,
                r => $"Trap hai đầu xác nhận tại {r.ConfirmedTime:yyyy-MM-dd HH:mm}"
            );
        }

        /// <summary>
        /// Cập nhật Secondary Test sau AR.
        /// </summary>
        public void UpdateWithST(SecondaryTestEvent st)
        {
            TryTransition(
                st,
                WyckoffPhase.AR,
                WyckoffPhase.ST,
                e => true,
                e => e.Time,
                e => $"ST xác nhận tại {e.Time:yyyy-MM-dd HH:mm}"
            );
        }

        /// <summary>
        /// Cập nhật SOS_ACC (Sign of Strength) sau TRAP.
        /// </summary>
        public void UpdateWithSOS(StrengthSignalEvent ss)
        {
            TryTransition(
                ss,
                WyckoffPhase.TRAP,
                WyckoffPhase.SOS_ACC,
                e => e.IsBreakout && e.IsValid,
                e => e.Time,
                e => $"SOS xác nhận tại {e.Time:yyyy-MM-dd HH:mm}");
        }

        /// <summary>
        /// Cập nhật LPS_ACC (Last Point of Support) sau SOS_ACC.
        /// </summary>
        public void UpdateWithLPS(RetestConfirmationEvent lp)
        {
            TryTransition(
                lp,
                WyckoffPhase.SOS_ACC,
                WyckoffPhase.LPS_ACC,
                e => true,
                e => e.Time,
                e => $"LPS xác nhận tại {e.Time:yyyy-MM-dd HH:mm}");
        }

        /// <summary>
        /// Cập nhật Upthrust (phân phối) sau TRAP.
        /// </summary>
        public void UpdateWithUpthrust(StrengthSignalEvent ut)
            => TryTransition(ut, WyckoffPhase.TRAP, WyckoffPhase.Upthrust,
                e => e.IsBreakout && !e.IsValid,
                e => e.Time,
                e => $"[Distr] Upthrust tại {e.Time:yyyy-MM-dd HH:mm}");

        /// <summary>
        /// Cập nhật SOS_DIST (Sign of Weakness) sau Upthrust.
        /// </summary>
        public void UpdateWithSOSY(StrengthSignalEvent sy)
            => TryTransition(sy, WyckoffPhase.Upthrust, WyckoffPhase.SOS_DIST,
                e => e.IsBreakout && !e.IsValid,
                e => e.Time,
                e => $"[Distr] SOS_DIST tại {e.Time:yyyy-MM-dd HH:mm}");

        /// <summary>
        /// Cập nhật LPS_DIST (Last Point of Supply) sau SOS_DIST.
        /// </summary>
        public void UpdateWithLPSY(LPSEvent lp)
            => TryTransition(lp, WyckoffPhase.SOS_DIST, WyckoffPhase.LPS_DIST,
                e => e.IsLPSY,
                e => e.Time,
                e => $"[Distr] LPS_DIST tại {e.Time:yyyy-MM-dd HH:mm}");

        /// <summary>
        /// Cập nhật Spring (giả phá đáy) sau TRAP.
        /// </summary>
        public void UpdateWithSpring(SpringEvent sp)
            => TryTransition(sp,
                             WyckoffPhase.TRAP,
                             WyckoffPhase.Spring,
                             e => e.IsValidSpring,
                             e => e.Time,
                             e => $"[Accu] Spring tại {e.Time:yyyy-MM-dd HH:mm}");

        // Có thể mở rộng thêm các detector khác như SOS, Spring, v.v.

        /// <summary>
        /// Đặt lại bộ phát hiện, ghi nhận sự kiện Reset.
        /// </summary>
        public void Reset()
        {
            _logger?.Invoke($"[WyckoffPhaseDetector] Reset phase detector");
            PhaseHistory.Add((WyckoffPhase.None, DateTime.Now, "Reset"));
            CurrentPhase = WyckoffPhase.None;
            PhaseStartTime = null;
            PhaseNote = null;
        }
    }
}
