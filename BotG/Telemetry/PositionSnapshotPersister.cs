using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Telemetry
{
    /// <summary>
    /// Persists position-level snapshots to CSV
    /// Separate file from account-level risk snapshots for better performance
    /// </summary>
    public class PositionSnapshotPersister
    {
        private readonly string _filePath;
        private readonly object _lock = new object();
        private DateTime _lastRotation = DateTime.UtcNow;
        private const long MaxFileSizeBytes = 50 * 1024 * 1024; // 50MB rotation threshold

        public PositionSnapshotPersister(string folder, string fileName)
        {
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, fileName);
            EnsureHeader();
        }

        private void EnsureHeader()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                {
                    var header = "timestamp_utc,symbol,direction,volume,entry_price,current_price,unrealized_pnl,pips,used_margin,open_time,position_id" + Environment.NewLine;
                    File.WriteAllText(_filePath, header);
                }
            }
        }

        /// <summary>
        /// Persist snapshots of all open positions
        /// </summary>
        public void Persist(IEnumerable<PositionSnapshot> positions)
        {
            if (positions == null || !positions.Any())
                return;

            var timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            var lines = new List<string>();

            foreach (var pos in positions)
            {
                var line = $"{timestamp},{pos.ToCsvRow()}";
                lines.Add(line);
            }

            lock (_lock)
            {
                try
                {
                    // Check file size for rotation
                    if (File.Exists(_filePath))
                    {
                        var fileInfo = new FileInfo(_filePath);
                        if (fileInfo.Length > MaxFileSizeBytes)
                        {
                            RotateFile();
                        }
                    }

                    // Append all position snapshots
                    File.AppendAllLines(_filePath, lines);
                }
                catch (IOException ex)
                {
                    // Log file lock errors separately
                    LogFileLockError(ex);
                }
                catch (Exception ex)
                {
                    // Log other errors
                    LogError(ex);
                }
            }
        }

        /// <summary>
        /// Rotate file when it exceeds size threshold
        /// </summary>
        private void RotateFile()
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                var directory = Path.GetDirectoryName(_filePath) ?? ".";
                var fileName = Path.GetFileNameWithoutExtension(_filePath);
                var extension = Path.GetExtension(_filePath);
                var rotatedPath = Path.Combine(directory, $"{fileName}_{timestamp}{extension}");

                // Rename current file
                File.Move(_filePath, rotatedPath);

                // Compress rotated file asynchronously (best effort)
                System.Threading.Tasks.Task.Run(() => CompressFile(rotatedPath));

                // Create new file with header
                EnsureHeader();

                _lastRotation = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // Don't fail persist if rotation fails
                LogError(new Exception("File rotation failed", ex));
            }
        }

        /// <summary>
        /// Compress old file using GZip (async, best effort)
        /// </summary>
        private void CompressFile(string filePath)
        {
            try
            {
                var gzipPath = filePath + ".gz";
                using (var input = File.OpenRead(filePath))
                using (var output = File.Create(gzipPath))
                using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress))
                {
                    input.CopyTo(gzip);
                }

                // Delete original after successful compression
                File.Delete(filePath);
            }
            catch
            {
                // Compression failed, keep original file
            }
        }

        /// <summary>
        /// Log file lock errors with retry suggestions
        /// </summary>
        private void LogFileLockError(IOException ex)
        {
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "position_snapshot_locks.log");
                var message = $"[{DateTime.UtcNow:o}] FILE LOCK: {ex.Message}{Environment.NewLine}";
                File.AppendAllText(logPath, message);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        /// <summary>
        /// Log general errors
        /// </summary>
        private void LogError(Exception ex)
        {
            try
            {
                var logPath = Path.Combine(Path.GetDirectoryName(_filePath) ?? ".", "position_snapshot_errors.log");
                var message = $"[{DateTime.UtcNow:o}] ERROR: {ex}{Environment.NewLine}";
                File.AppendAllText(logPath, message);
            }
            catch
            {
                // Ignore logging errors
            }
        }

        /// <summary>
        /// Get file info for monitoring
        /// </summary>
        public (long size, DateTime lastWrite, bool needsRotation) GetFileInfo()
        {
            lock (_lock)
            {
                if (!File.Exists(_filePath))
                    return (0, DateTime.MinValue, false);

                var info = new FileInfo(_filePath);
                return (info.Length, info.LastWriteTime, info.Length > MaxFileSizeBytes);
            }
        }
    }
}
