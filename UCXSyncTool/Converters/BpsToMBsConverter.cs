using System;
using System.Globalization;
using System.Windows.Data;

namespace UCXSyncTool
{
    // Converts bytes-per-second (double or long) to a formatted MB/s string.
    public class BpsToMBsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double bps = 0.0;
                if (value is double d) bps = d;
                else if (value is float f) bps = f;
                else if (value is long l) bps = l;
                else if (value is int i) bps = i;
                else if (value is null) return "0.00 MB/s";

                double mbs = bps / 1024.0 / 1024.0;
                return string.Format(culture, "{0:F2} MB/s", mbs);
            }
            catch
            {
                return "-";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
