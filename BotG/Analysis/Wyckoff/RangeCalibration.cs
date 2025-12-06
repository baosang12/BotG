using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Analysis.Wyckoff
{
    /// <summary>
    /// Simple calibration utility: parses range JSONL log, aggregates statistics per phase and trigger.
    /// Outputs distribution metrics and naive suggested thresholds.
    /// </summary>
    public static class RangeCalibration
    {
        private class AnnotatedRows
        {
            public string File { get; set; }
            public string Timeframe { get; set; }
            public List<Row> Rows { get; set; } = new();
        }
        private class Row
        {
            public DateTime ts { get; set; }
            public string line { get; set; }
            public string phase { get; set; }
            public bool? mini { get; set; }
            public double? occ { get; set; }
            public double? comp { get; set; }
            public double? compInv { get; set; }
            public double? drift { get; set; }
            public double? driftNorm { get; set; }
            public double? width { get; set; }
            public double? upper { get; set; }
            public double? lower { get; set; }
            public int? expCount { get; set; }
            public bool? locked { get; set; }
            public double? sizeFactor { get; set; }
            public int? trigger { get; set; }
            public int? fail { get; set; }
            public int? idx { get; set; }
            public string trigType { get; set; }
            public string trigDir { get; set; }
            public double? trigBody { get; set; }
            public double? trigRangeFac { get; set; }
            public double? trigVolSpike { get; set; }
        }

        public static void Run(string file)
        {
            if (!File.Exists(file)) { Console.WriteLine($"[Calibration] File not found: {file}"); return; }
            Console.WriteLine($"[Calibration] Loading {file}");
            var rows = new List<Row>(capacity: 16384);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            int lineNo = 0; int bad = 0;
            foreach (var ln in File.ReadLines(file))
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(ln)) continue;
                try { rows.Add(JsonSerializer.Deserialize<Row>(ln, opts)); }
                catch { bad++; }
            }
            Console.WriteLine($"[Calibration] Parsed ok={rows.Count} bad={bad}");
            if (rows.Count == 0) { Console.WriteLine("[Calibration] No data."); return; }

            // Filter only rows with phase + comp etc. (structured snapshots)
            var snap = rows.Where(r => r.phase != null && r.comp.HasValue).ToList();
            if (snap.Count == 0) { Console.WriteLine("[Calibration] No structured rows with metrics."); return; }

            // Per phase aggregates
            Console.WriteLine("\n=== Phase Metrics ===");
            foreach (var g in snap.GroupBy(r => r.phase))
            {
                EmitPhaseStats(g.Key, g);
            }

            // Trigger metrics (if any)
            var trigRows = rows.Where(r => !string.IsNullOrEmpty(r.trigType)).ToList();
            Console.WriteLine("\n=== Trigger Metrics ===");
            if (trigRows.Count == 0)
            {
                Console.WriteLine("No trigger entries with trigType yet. Collect breakout examples to calibrate breakout filters.");
            }
            else
            {
                foreach (var g in trigRows.GroupBy(r => r.trigType))
                {
                    EmitTriggerStats(g.Key, g);
                }
                SuggestBreakoutThresholds(trigRows);
            }
        }

        public static void RunMulti(IEnumerable<string> files)
        {
            var annotated = new List<AnnotatedRows>();
            foreach (var f in files)
            {
                if (!File.Exists(f)) { Console.WriteLine($"[Calibration] Skip missing {f}"); continue; }
                var ar = new AnnotatedRows { File = f };
                LoadFileInto(f, ar.Rows, out int bad);
                ar.Timeframe = DetectTimeframe(ar.Rows) ?? InferTfFromName(f);
                Console.WriteLine($"[Calibration] Loaded {f} rows={ar.Rows.Count} bad={bad} tf={ar.Timeframe}");
                annotated.Add(ar);
            }
            if (annotated.Count == 0) { Console.WriteLine("[Calibration] No valid files."); return; }
            // Combined snapshot
            var combined = annotated.SelectMany(a => a.Rows.Select(r => (a.Timeframe, r))).Where(x => x.r.phase != null && x.r.comp.HasValue).ToList();
            Console.WriteLine("\n=== Combined Phase Metrics (All Timeframes) ===");
            foreach (var g in combined.GroupBy(x => x.r.phase))
            {
                EmitPhaseStats(g.Key + " (ALL)", g.Select(x => x.r));
            }
            Console.WriteLine("\n=== Per Timeframe Phase Metrics ===");
            foreach (var tfGroup in combined.GroupBy(x => x.Timeframe))
            {
                Console.WriteLine($"-- Timeframe {tfGroup.Key} --");
                foreach (var phaseGroup in tfGroup.GroupBy(x => x.r.phase))
                {
                    EmitPhaseStats(phaseGroup.Key, phaseGroup.Select(x => x.r));
                }
            }
            // Trigger metrics per timeframe
            var trigAll = annotated.SelectMany(a => a.Rows.Select(r => (a.Timeframe, r))).Where(x => !string.IsNullOrEmpty(x.r.trigType)).ToList();
            Console.WriteLine("\n=== Trigger Metrics Per Timeframe ===");
            if (trigAll.Count == 0)
            {
                Console.WriteLine("No trigger entries yet across provided files.");
                return;
            }
            foreach (var tf in trigAll.GroupBy(x => x.Timeframe))
            {
                Console.WriteLine($"-- TF {tf.Key} --");
                foreach (var ttype in tf.GroupBy(x => x.r.trigType))
                {
                    EmitTriggerStats(ttype.Key, ttype.Select(x => x.r));
                }
            }
            // Cross-timeframe suggestions using all
            SuggestBreakoutThresholds(trigAll.Select(x => x.r).ToList());
        }

        private static void LoadFileInto(string file, List<Row> rows, out int bad)
        {
            bad = 0; int lineNo = 0; var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var ln in File.ReadLines(file))
            {
                lineNo++; if (string.IsNullOrWhiteSpace(ln)) continue; try { rows.Add(JsonSerializer.Deserialize<Row>(ln, opts)); } catch { bad++; }
            }
        }

        private static string DetectTimeframe(List<Row> rows)
        {
            // Look into early BOOT lines "timeframe=Hour" etc.
            foreach (var r in rows.Take(30))
            {
                if (r.line != null && r.line.Contains("timeframe="))
                {
                    int idx = r.line.IndexOf("timeframe=");
                    if (idx >= 0)
                    {
                        var seg = r.line.Substring(idx + 10).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                        return seg?.Trim();
                    }
                }
            }
            return null;
        }

        private static string InferTfFromName(string file)
        {
            var name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            if (name.Contains("M15")) return "M15";
            if (name.Contains("H1") || name.Contains("H_1") || name.Contains("HOUR")) return "Hour";
            if (name.Contains("M5")) return "M5";
            return "UNK";
        }

        private static void EmitPhaseStats(string phase, IEnumerable<Row> rows)
        {
            var list = rows.ToList();
            List<double> Extract(Func<Row, double?> sel) => list.Select(sel).Where(v => v.HasValue).Select(v => v.Value).ToList();
            var compVals = Extract(r => r.comp); var driftVals = Extract(r => r.driftNorm); var widthVals = Extract(r => r.width); var occVals = Extract(r => r.occ);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0,-22} n={1,4} comp(mean/med p10-p90)={2:0.000}/{3:0.000} {4:0.000}-{5:0.000} driftNorm(mean/med)={6:0.00}/{7:0.00} width(med)={8:0.00000} occ(mean)={9:0.00} expCountMax={10}",
                phase, list.Count,
                Mean(compVals), Median(compVals), Percentile(compVals, 10), Percentile(compVals, 90),
                Mean(driftVals), Median(driftVals), Median(widthVals), Mean(occVals), list.Max(r => r.expCount ?? 0)));
        }

        private static void EmitTriggerStats(string trigType, IEnumerable<Row> rows)
        {
            var list = rows.ToList();
            List<double> Extract(Func<Row, double?> sel) => list.Select(sel).Where(v => v.HasValue).Select(v => v.Value).ToList();
            var body = Extract(r => r.trigBody); var rangeF = Extract(r => r.trigRangeFac); var vol = Extract(r => r.trigVolSpike);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "Trigger {0}: n={1} body(mean/med p25)={2:0.00}/{3:0.00}/{4:0.00} rangeFac(mean/med p25)={5:0.00}/{6:0.00}/{7:0.00} volSpike(mean/med p25)={8:0.00}/{9:0.00}/{10:0.00}",
                trigType, list.Count,
                Mean(body), Median(body), Percentile(body, 25),
                Mean(rangeF), Median(rangeF), Percentile(rangeF, 25),
                Mean(vol), Median(vol), Percentile(vol, 25)));
        }

        private static void SuggestBreakoutThresholds(List<Row> trigRows)
        {
            var body = trigRows.Where(r => r.trigBody.HasValue).Select(r => r.trigBody.Value).ToList();
            var rangeF = trigRows.Where(r => r.trigRangeFac.HasValue).Select(r => r.trigRangeFac.Value).ToList();
            var vol = trigRows.Where(r => r.trigVolSpike.HasValue).Select(r => r.trigVolSpike.Value).ToList();
            if (body.Count < 5 || rangeF.Count < 5 || vol.Count < 5)
            {
                Console.WriteLine("[Suggest] Not enough breakout trigger samples for robust threshold suggestions (need >=5 each).");
                return;
            }
            // Naive suggestion: set minimum ~ 25th percentile to keep roughly top 75% signals
            double bodyMin = Percentile(body, 25);
            double rangeMin = Percentile(rangeF, 25);
            double volMin = Percentile(vol, 25);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[Suggest] Breakout quality mins -> BodyRatioMin≈{0:0.00} RangeFactorMin≈{1:0.00} VolumeSpikeMin≈{2:0.00}", bodyMin, rangeMin, volMin));
        }

        // Basic stats helpers
        private static double Mean(List<double> v) => v.Count == 0 ? double.NaN : v.Average();
        private static double Median(List<double> v)
        {
            if (v.Count == 0) return double.NaN;
            var a = v.OrderBy(x => x).ToList();
            int m = a.Count / 2; return a.Count % 2 == 1 ? a[m] : (a[m - 1] + a[m]) / 2.0;
        }
        private static double Percentile(List<double> v, double p)
        {
            if (v.Count == 0) return double.NaN;
            var a = v.OrderBy(x => x).ToList();
            if (p <= 0) return a[0]; if (p >= 100) return a[^1];
            double rank = (p / 100.0) * (a.Count - 1);
            int lo = (int)Math.Floor(rank); int hi = (int)Math.Ceiling(rank);
            if (lo == hi) return a[lo];
            double f = rank - lo; return a[lo] + (a[hi] - a[lo]) * f;
        }
    }
}
