#nullable enable
namespace ErpNet.FP.Core.Drivers.RoTremol
{
    using System;
    using System.Collections.Generic;
    using ErpNet.FP.Core.Configuration;
    using Serilog;

    /// <summary>
    /// Driver "ro.zk.zfp": Tremol devices with Romanian firmware.
    /// URI examples:
    ///   ro.zk.zfp.com://COM9
    ///   ro.zk.zfp.tcp://192.168.1.60:8000
    /// </summary>
    public class RoTremolZfpFiscalPrinterDriver : FiscalPrinterDriver
    {
        public override string DriverName => "ro.zk.zfp";

        public override IFiscalPrinter Connect(
            IChannel channel,
            ServiceOptions serviceOptions,
            bool autoDetect = true,
            IDictionary<string, string>? options = null)
        {
            var fiscalPrinter = new RoTremolZfpFiscalPrinter(channel, serviceOptions, options);
            lock (channel)
            {
                var (serial, version, vatNumber, fmNumber, _) = fiscalPrinter.ReadIdentity();
                Log.Information($"RawDeviceInfo({channel.Descriptor}): serial='{serial}' version='{version}' vat='{vatNumber}' fm='{fmNumber}'");

                fiscalPrinter.Info = ParseDeviceInfo(serial, version, fmNumber, autoDetect);
                fiscalPrinter.Info.TaxIdentificationNumber = vatNumber;

                var card = fiscalPrinter.DetectCardPaymentNumber();
                if (card != null) fiscalPrinter.CardPaymentNumber = card;

                fiscalPrinter.Info.SupportedPaymentTypes = fiscalPrinter.GetSupportedPaymentTypes();
                fiscalPrinter.Info.SupportsSubTotalAmountModifiers = true;
                fiscalPrinter.Info.SupportsPeriodReport = true;
                // Fiscal invoice with buyer CIF via the "special customer document" (command 30h),
                // Tremol RO protocol 2.6.4 (p.46). Source: Tremol ZFP SDK RO (protocol_description_RO).
                fiscalPrinter.Info.SupportsInvoice = true;
                fiscalPrinter.Info.InvoiceNumberAssignment = NumberAssignment.DeviceAssigned;
                serviceOptions.ReconfigurePrinterConstants(fiscalPrinter.Info);
                serviceOptions.ReconfigurePrinterOptions(fiscalPrinter.Info);
                return fiscalPrinter;
            }
        }

        /// <summary>
        /// A Romanian Tremol answers 21h with "Ver 1.00,TRM25-RO" (Bulgarian: "1;784;...;TREMOL M20;...")
        /// and 60h with only the serial number.
        /// </summary>
        public static bool LooksRomanian(string version)
        {
            var v = version.Trim().ToUpperInvariant();
            return !v.Contains(';')
                && (v.EndsWith("-RO", StringComparison.Ordinal) || v.Contains("-RO,") || v.Contains("-RO ") || v.EndsWith(" RO", StringComparison.Ordinal));
        }

        public DeviceInfo ParseDeviceInfo(string serial, string version, string fmNumber, bool autoDetect)
        {
            if (string.IsNullOrWhiteSpace(serial) || serial.Contains(';'))
            {
                throw new InvalidDeviceInfoException($"unrecognized serial number '{serial}' for '{DriverName}'");
            }
            if (autoDetect && !LooksRomanian(version))
            {
                throw new InvalidDeviceInfoException($"version '{version}' is not a Romanian Tremol firmware for '{DriverName}'");
            }

            // "Ver 1.00,TRM25-RO" -> firmware "Ver 1.00", model "TRM25-RO"
            var parts = version.Split(',');
            var firmware = parts.Length > 1 ? parts[0].Trim() : version.Trim();
            var model = parts.Length > 1 ? parts[^1].Trim() : "Tremol";

            return new DeviceInfo
            {
                SerialNumber = serial.Trim(),
                FiscalMemorySerialNumber = string.IsNullOrWhiteSpace(fmNumber) ? serial.Trim() : fmNumber.Trim(),
                Model = model,
                FirmwareVersion = firmware,
                Manufacturer = "Tremol",
                CommentTextMaxLength = 30,
                ItemTextMaxLength = 30,
                OperatorPasswordMaxLength = 4,
            };
        }
    }
}
