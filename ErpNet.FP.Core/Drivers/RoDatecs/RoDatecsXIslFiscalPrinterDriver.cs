#nullable enable
namespace ErpNet.FP.Core.Drivers.RoDatecs
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using ErpNet.FP.Core.Configuration;
    using Serilog;

    /// <summary>
    /// Driver "ro.dt.x.isl": Datecs devices with Romanian firmware, X dialect.
    /// URI examples:
    ///   ro.dt.x.isl.com://COM3
    ///   ro.dt.x.isl.tcp://192.168.1.50:3999
    /// </summary>
    public class RoDatecsXIslFiscalPrinterDriver : FiscalPrinterDriver
    {
        public override string DriverName => "ro.dt.x.isl";

        private static readonly string[] KnownModelPrefixes = { "DP", "WP", "FP", "FMP" };
        private static readonly string[] BulgarianSerialPrefixes = { "DT", "DA" };

        public override IFiscalPrinter Connect(
            IChannel channel,
            ServiceOptions serviceOptions,
            bool autoDetect = true,
            IDictionary<string, string>? options = null)
        {
            var fiscalPrinter = new RoDatecsXIslFiscalPrinter(channel, serviceOptions, options);
            var rawDeviceInfoCacheKey = $"ro.x.isl.{channel.Descriptor}.{DriverName}";
            lock (channel)
            {
                var rawDeviceInfo = Cache.Get(rawDeviceInfoCacheKey);
                if (rawDeviceInfo == null || autoDetect)
                {
                    (rawDeviceInfo, _) = fiscalPrinter.GetRawDeviceInfo();
                    Log.Information($"RawDeviceInfo({channel.Descriptor}): {rawDeviceInfo}");
                    Cache.Store(rawDeviceInfoCacheKey, rawDeviceInfo, TimeSpan.FromSeconds(30));
                }
                fiscalPrinter.Info = ParseDeviceInfo(rawDeviceInfo, autoDetect);
                var (taxIdentificationNumber, _) = fiscalPrinter.GetTaxIdentificationNumber();
                fiscalPrinter.Info.TaxIdentificationNumber = taxIdentificationNumber;
                fiscalPrinter.Info.SupportedPaymentTypes = fiscalPrinter.GetSupportedPaymentTypes();
                fiscalPrinter.Info.SupportsSubTotalAmountModifiers = true;
                fiscalPrinter.Info.SupportsPeriodReport = true;
                serviceOptions.ReconfigurePrinterConstants(fiscalPrinter.Info);
                serviceOptions.ReconfigurePrinterOptions(fiscalPrinter.Info);
                return fiscalPrinter;
            }
        }

        public class RawDeviceInfo
        {
            public string Model = string.Empty;
            public string FirmwareVersion = string.Empty;
            public string SerialNumber = string.Empty;
            public string FiscalMemorySerialNumber = string.Empty;
        }

        /// <summary>
        /// Parses the answer of command 90 in both layouts:
        ///  - comma layout:  {Name},{FwRev} {FwDate} {FwTime},{Checksum},{Sw},{SerialNumber}[,{FMNumber}]
        ///  - tab layout:    {ErrorCode}\t{Name}\t{FwRev}\t{FwDate}\t{FwTime}\t{Checksum}\t{Sw}\t{SerialNumber}[\t{FMNumber}]
        /// The fiscal memory number is optional and the serial number length is not fixed
        /// (Romanian serials are not the 8-char Bulgarian ones).
        /// Returns null when the answer cannot be a Datecs device info.
        /// </summary>
        public static RawDeviceInfo? TryParseRawDeviceInfo(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = raw.Trim().TrimEnd('\t', ',');

            if (raw.Contains(','))
            {
                var f = raw.Split(',').Select(s => s.Trim()).ToArray();
                if (f.Length < 5 || f[0].Length == 0 || f[4].Length == 0) return null;
                return new RawDeviceInfo
                {
                    Model = f[0],
                    FirmwareVersion = f[1],
                    SerialNumber = f[4],
                    FiscalMemorySerialNumber = f.Length > 5 ? f[5] : string.Empty
                };
            }

            if (raw.Contains('\t'))
            {
                var f = raw.Split('\t').Select(s => s.Trim()).ToList();
                // Drop the leading error code, if present.
                if (f.Count > 0 && int.TryParse(f[0], out _)) f.RemoveAt(0);
                if (f.Count < 7 || f[0].Length == 0 || f[6].Length == 0) return null;
                return new RawDeviceInfo
                {
                    Model = f[0],
                    FirmwareVersion = $"{f[1]} {f[2]} {f[3]}".Trim(),
                    SerialNumber = f[6],
                    FiscalMemorySerialNumber = f.Count > 7 ? f[7] : string.Empty
                };
            }

            return null;
        }

        /// <summary>
        /// True for the answer of a Bulgarian X-firmware device (8-char DT/DA serial plus an
        /// 8-digit fiscal memory number). Those devices belong to bg.dt.x.isl.
        /// </summary>
        public static bool LooksBulgarian(RawDeviceInfo info)
        {
            return info.SerialNumber.Length == 8
                && BulgarianSerialPrefixes.Any(p => info.SerialNumber.StartsWith(p, StringComparison.Ordinal))
                && info.FiscalMemorySerialNumber.Length == 8
                && info.FiscalMemorySerialNumber.All(char.IsDigit);
        }

        protected DeviceInfo ParseDeviceInfo(string rawDeviceInfo, bool autoDetect)
        {
            var parsed = TryParseRawDeviceInfo(rawDeviceInfo)
                ?? throw new InvalidDeviceInfoException($"unrecognized device info '{rawDeviceInfo}' for '{DriverName}'");

            if (autoDetect)
            {
                if (!KnownModelPrefixes.Any(p => parsed.Model.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDeviceInfoException($"model '{parsed.Model}' incompatible with '{DriverName}'");
                }
                if (LooksBulgarian(parsed))
                {
                    throw new InvalidDeviceInfoException($"Bulgarian firmware detected, use bg.dt.x.isl instead of '{DriverName}'");
                }
            }

            var printColumns = GetPrintColumnsOfModel(parsed.Model);
            return new DeviceInfo
            {
                SerialNumber = parsed.SerialNumber,
                // Not every Romanian firmware reports it; the serial number identifies the device then.
                FiscalMemorySerialNumber = string.IsNullOrWhiteSpace(parsed.FiscalMemorySerialNumber)
                    ? parsed.SerialNumber
                    : parsed.FiscalMemorySerialNumber,
                Model = parsed.Model,
                FirmwareVersion = parsed.FirmwareVersion,
                Manufacturer = "Datecs",
                CommentTextMaxLength = printColumns - 2,
                ItemTextMaxLength = 72,
                OperatorPasswordMaxLength = 8,
                SupportPaymentTerminal = true
            };
        }

        protected static int GetPrintColumnsOfModel(string modelName)
        {
            var m = modelName.ToUpperInvariant();
            if (m.StartsWith("FMP-55")) return 32;
            if (m.StartsWith("FP-700") || m.StartsWith("FMP-350") || m.StartsWith("FP-800")) return 48;
            return 42;   // DP-25, DP-150, WP-50, WP-500
        }
    }
}
