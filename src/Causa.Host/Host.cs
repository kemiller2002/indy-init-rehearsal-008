// Owner: integration. The single [JSExport] entry point of the Causa engine (Rehearsal 7 finding KD-007: F# does
// not run the JSExport generator). Marshalling glue only: a JSON string in, a JSON string out. No state, no rule,
// no decision lives here; Causa.App.Program owns all of it.
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Causa.Host;

[SupportedOSPlatform("browser")]
public static partial class HostShim
{
    /// <summary>One Limen BrowserToEngineMessage (JSON) in, one EngineToBrowserMessage (JSON) out.</summary>
    [JSExport]
    public static string Dispatch(string message) => Causa.App.Program.dispatch(message);

    // The WebAssembly SDK needs an entry point; the page drives the engine only through Dispatch.
    public static void Main() { }
}
