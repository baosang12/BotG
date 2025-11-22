using System;
using System.Collections.Generic;

namespace BotG.RiskManager
{
    /// <summary>
    /// Quản lý cấp độ tài khoản (Account Level) dựa trên số dư (balance).
    /// </summary>
    public class AccountLevelManager
    {
        private readonly List<AccountLevel> _levels;

        /// <param name="levels">Danh sách cấp độ tài khoản, định nghĩa min/max balance và % rủi ro.</param>
        public AccountLevelManager(IEnumerable<AccountLevel> levels)
        {
            _levels = new List<AccountLevel>(levels);
            // Sắp xếp theo MinBalance tăng dần
            _levels.Sort((a, b) => a.MinBalance.CompareTo(b.MinBalance));
        }

        /// <summary>
        /// Lấy cấp độ phù hợp với số dư hiện tại.
        /// </summary>
        public AccountLevel GetLevel(double balance)
        {
            AccountLevel result = null;
            foreach (var lvl in _levels)
            {
                if (balance >= lvl.MinBalance && balance <= lvl.MaxBalance)
                {
                    result = lvl;
                    break;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// Định nghĩa thông tin cho mỗi cấp độ tài khoản.
    /// </summary>
    public class AccountLevel
    {
        public int Level { get; set; }
        public double MinBalance { get; set; }
        public double MaxBalance { get; set; }
        public double RiskPercent { get; set; }
        public double LevelUpMultiplier { get; set; }
    }
}
