namespace ErpNet.FP.Core
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using Newtonsoft.Json;
    using Serilog;

    /// <summary>
    /// Persistent journal of receipts already sent to a fiscal device, keyed by the
    /// client's unique sale number (USN).
    /// <para>
    /// The bridge is called synchronously by the POS/ERP. If the HTTP call times out after
    /// the device already printed the receipt, the client retries with the same USN.
    /// Without this journal the device would print a second fiscal receipt for the same sale.
    /// </para>
    /// <para>
    /// States:
    ///  - Pending: the receipt was sent to the device and no final answer was recorded
    ///    (process crash, power loss, lost connection mid-print). It is NOT reprinted
    ///    automatically, because it may already be in the fiscal memory.
    ///  - Printed: the device confirmed the receipt; a retry returns the stored result.
    /// A failed (aborted) receipt is removed, so the client can safely retry it.
    /// </para>
    /// </summary>
    public class UsnJournal
    {
        public enum EntryState { Pending, Printed }

        public class Entry
        {
            public string UniqueSaleNumber { get; set; } = string.Empty;
            public EntryState State { get; set; }
            public DateTime CreatedAt { get; set; }
            public string ReceiptNumber { get; set; } = string.Empty;
            public DateTime ReceiptDateTime { get; set; }
            public decimal ReceiptAmount { get; set; }
            public string FiscalMemorySerialNumber { get; set; } = string.Empty;
        }

        private readonly object syncLock = new object();
        private readonly string filePath;
        private readonly int maxEntries;
        private Dictionary<string, Entry> entries;

        public UsnJournal(string filePath, int maxEntries = 20000)
        {
            this.filePath = filePath;
            this.maxEntries = maxEntries;
            entries = Load();
        }

        /// <summary>
        /// Builds the default journal location for a device: next to the executable,
        /// in a "usn-journal" folder, one file per device serial number.
        /// </summary>
        public static string DefaultPathFor(string serialNumber)
        {
            var safeSerial = string.Concat((serialNumber ?? "unknown")
                .Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            return Path.Combine(AppContext.BaseDirectory, "usn-journal", $"{safeSerial}.json");
        }

        public Entry? Find(string uniqueSaleNumber)
        {
            lock (syncLock)
            {
                return entries.TryGetValue(uniqueSaleNumber, out var entry) ? entry : null;
            }
        }

        public void MarkPending(string uniqueSaleNumber)
        {
            lock (syncLock)
            {
                entries[uniqueSaleNumber] = new Entry
                {
                    UniqueSaleNumber = uniqueSaleNumber,
                    State = EntryState.Pending,
                    CreatedAt = DateTime.Now
                };
                Save();
            }
        }

        public void MarkPrinted(string uniqueSaleNumber, ReceiptInfo receiptInfo)
        {
            lock (syncLock)
            {
                entries[uniqueSaleNumber] = new Entry
                {
                    UniqueSaleNumber = uniqueSaleNumber,
                    State = EntryState.Printed,
                    CreatedAt = DateTime.Now,
                    ReceiptNumber = receiptInfo.ReceiptNumber,
                    ReceiptDateTime = receiptInfo.ReceiptDateTime,
                    ReceiptAmount = receiptInfo.ReceiptAmount,
                    FiscalMemorySerialNumber = receiptInfo.FiscalMemorySerialNumber
                };
                Trim();
                Save();
            }
        }

        public void Remove(string uniqueSaleNumber)
        {
            lock (syncLock)
            {
                if (entries.Remove(uniqueSaleNumber))
                {
                    Save();
                }
            }
        }

        private void Trim()
        {
            if (entries.Count <= maxEntries) return;
            var toRemove = entries.Values
                .Where(e => e.State == EntryState.Printed)
                .OrderBy(e => e.CreatedAt)
                .Take(entries.Count - maxEntries)
                .Select(e => e.UniqueSaleNumber)
                .ToList();
            foreach (var key in toRemove) entries.Remove(key);
        }

        private Dictionary<string, Entry> Load()
        {
            try
            {
                if (File.Exists(filePath))
                {
                    var list = JsonConvert.DeserializeObject<List<Entry>>(File.ReadAllText(filePath));
                    if (list != null)
                    {
                        return list
                            .Where(e => !string.IsNullOrEmpty(e.UniqueSaleNumber))
                            .GroupBy(e => e.UniqueSaleNumber)
                            .ToDictionary(g => g.Key, g => g.Last());
                    }
                }
            }
            catch (Exception ex)
            {
                // A corrupt journal must not stop the bridge, but it must be visible.
                Log.Error($"Cannot read USN journal {filePath}: {ex.Message}");
                try { File.Copy(filePath, filePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            }
            return new Dictionary<string, Entry>();
        }

        private void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = filePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(entries.Values.ToList(), Formatting.Indented));
                // Atomic replace so a crash never leaves a half-written journal.
                File.Move(tmp, filePath, true);
            }
            catch (Exception ex)
            {
                Log.Error($"Cannot write USN journal {filePath}: {ex.Message}");
            }
        }
    }
}
