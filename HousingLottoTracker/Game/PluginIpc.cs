using System;
using Dalamud.Plugin;

namespace HousingLottoTracker.Game;

// Thin wrappers around AutoRetainer and Lifestream IPC, mirroring FC Tracker's
// approach. We call the raw IPC subscriber strings directly so we don't bundle
// their assemblies; every call is guarded so a missing plugin just no-ops.
public static class PluginIpc
{
    private static IDalamudPluginInterface Pi => Plugin.PluginInterface;

    // ---- AutoRetainer ----

    public static bool IsAutoRetainerAvailable()
    {
        try
        {
            Pi.GetIpcSubscriber<System.Collections.Generic.List<ulong>>("AutoRetainer.GetRegisteredCIDs").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    // ---- Lifestream ----

    public static bool IsLifestreamAvailable()
    {
        try
        {
            Pi.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc();
            return true;
        }
        catch { return false; }
    }

    // Sends the player to a housing address. Lifestream's ExecuteCommand runs
    // "/li <args>"; documented format is "<World> <District> <Ward> <Plot>" with
    // plain numbers (e.g. "Ultros Mist 1 1").
    public static bool LifestreamGoToAddress(string world, string district, int ward, int plot)
    {
        try
        {
            var args = $"{world} {district} {ward} {plot}";
            Pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand").InvokeAction(args);
            return true;
        }
        catch { return false; }
    }
}
