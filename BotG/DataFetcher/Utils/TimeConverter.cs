using System;
using System.Globalization;

namespace DataFetcher.Utils
{
    public static class TimeConverter
    {
        public static DateTime ToUtc(DateTime local) => local.ToUniversalTime();
        public static DateTime ToLocal(DateTime utc) => utc.ToLocalTime();

        /// <summary>
        /// Chuyển đổi DateTime sang một múi giờ cụ thể.
        /// </summary>
        public static DateTime ConvertToTimeZone(DateTime dt, string timeZoneId)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            return TimeZoneInfo.ConvertTimeFromUtc(dt.ToUniversalTime(), tz);
        }

        /// <summary>
        /// Chuẩn hóa timestamp từ chuỗi với định dạng và múi giờ.
        /// </summary>
        public static DateTime ParseTimestamp(string timestamp, string format = "yyyy-MM-dd HH:mm:ss", string timeZoneId = "UTC")
        {
            if (DateTime.TryParseExact(timestamp, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                return ConvertToTimeZone(dt, timeZoneId);
            }
            return DateTime.MinValue;
        }
    }
}
