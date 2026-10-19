using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace HousingLottoTracker.Data;

// Machine-wide (per Windows user) store for bid records, computed from %APPDATA%
// directly so all clients share one file regardless of --roamingPath. Same
// concurrency model as FC Tracker: exclusive-lock read-merge-write, merging by the
// bid Key so multiboxing clients never clobber each other's rows.
public sealed class SharedStore
{
    private readonly string path;
    private static readonly object GateLock = new();

    public SharedStore(bool useShared, string overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            path = overridePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            path = Path.Combine(appData, "XIVLauncher", "pluginConfigs", "HousingLottoTracker", "shared.json");
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public string Path_ => path;

    private sealed class StoreFile
    {
        public int Version = 1;
        public List<BidRecord> Bids = new();
    }

    public List<BidRecord> LoadAll()
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!File.Exists(path)) return new();
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var sr = new StreamReader(fs);
                    var json = sr.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(json)) return new();
                    var data = JsonConvert.DeserializeObject<StoreFile>(json);
                    return data?.Bids ?? new();
                }
                catch (IOException)
                {
                    Thread.Sleep(25 + attempt * 25);
                }
                catch (Exception)
                {
                    return new();
                }
            }
            return new();
        }
    }

    // One-time migration: recompute every stored bid's EntryCycleId from its
    // EntryDateUtc against the current cycle anchor. Used when the anchor itself is
    // corrected (e.g. the 1.1.0.0 one-day fix) so stored rows keep matching freshly
    // captured ones instead of duplicating. Returns how many rows changed.
    public int NormalizeCycleIds()
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                FileStream? fs = null;
                try
                {
                    if (!File.Exists(path)) return 0;
                    fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                    StoreFile store;
                    using (var sr = new StreamReader(fs, leaveOpen: true))
                    {
                        var json = sr.ReadToEnd();
                        store = string.IsNullOrWhiteSpace(json)
                            ? new StoreFile()
                            : (JsonConvert.DeserializeObject<StoreFile>(json) ?? new StoreFile());
                    }

                    var changed = 0;
                    foreach (var b in store.Bids)
                    {
                        var id = LottoCycle.CycleIdFor(b.EntryDateUtc);
                        if (b.EntryCycleId != id) { b.EntryCycleId = id; changed++; }
                    }
                    if (changed == 0) { fs.Dispose(); return 0; }

                    var outJson = JsonConvert.SerializeObject(store, Formatting.Indented);
                    fs.SetLength(0);
                    fs.Position = 0;
                    using (var sw = new StreamWriter(fs, leaveOpen: true))
                    {
                        sw.Write(outJson);
                        sw.Flush();
                    }
                    fs.Flush(true);
                    fs.Dispose();
                    return changed;
                }
                catch (IOException)
                {
                    fs?.Dispose();
                    Thread.Sleep(25 + attempt * 25);
                }
                catch (Exception)
                {
                    fs?.Dispose();
                    return 0;
                }
            }
            return 0;
        }
    }

    public void UpsertBid(BidRecord record)
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using var fs = new FileStream(
                        path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    StoreFile store;
                    using (var sr = new StreamReader(fs, leaveOpen: true))
                    {
                        var json = sr.ReadToEnd();
                        store = string.IsNullOrWhiteSpace(json)
                            ? new StoreFile()
                            : (JsonConvert.DeserializeObject<StoreFile>(json) ?? new StoreFile());
                    }

                    var idx = store.Bids.FindIndex(x => x.Key == record.Key);
                    if (idx >= 0) store.Bids[idx] = record;
                    else store.Bids.Add(record);

                    var outJson = JsonConvert.SerializeObject(store, Formatting.Indented);

                    fs.SetLength(0);
                    fs.Position = 0;
                    using (var sw = new StreamWriter(fs, leaveOpen: true))
                    {
                        sw.Write(outJson);
                        sw.Flush();
                    }
                    fs.Flush(true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(25 + attempt * 25);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    public void DeleteBid(string key)
    {
        lock (GateLock)
        {
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using var fs = new FileStream(
                        path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

                    StoreFile store;
                    using (var sr = new StreamReader(fs, leaveOpen: true))
                    {
                        var json = sr.ReadToEnd();
                        store = string.IsNullOrWhiteSpace(json)
                            ? new StoreFile()
                            : (JsonConvert.DeserializeObject<StoreFile>(json) ?? new StoreFile());
                    }

                    store.Bids.RemoveAll(x => x.Key == key);

                    var outJson = JsonConvert.SerializeObject(store, Formatting.Indented);
                    fs.SetLength(0);
                    fs.Position = 0;
                    using (var sw = new StreamWriter(fs, leaveOpen: true))
                    {
                        sw.Write(outJson);
                        sw.Flush();
                    }
                    fs.Flush(true);
                    return;
                }
                catch (IOException)
                {
                    Thread.Sleep(25 + attempt * 25);
                }
                catch (Exception)
                {
                    return;
                }
            }
        }
    }

    public void ClearAll()
    {
        lock (GateLock)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sw = new StreamWriter(fs);
                sw.Write(JsonConvert.SerializeObject(new StoreFile(), Formatting.Indented));
            }
            catch { /* ignore */ }
        }
    }
}
