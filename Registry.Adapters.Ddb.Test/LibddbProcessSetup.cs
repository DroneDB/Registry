using NUnit.Framework;
using Registry.Adapters.DroneDB;
using static Registry.Adapters.Ddb.Test.LibddbNative;

namespace Registry.Adapters.Ddb.Test;

/// <summary>
/// Registers libddb once for every fixture of this assembly, verbosely. libddb takes its process log level from the
/// first <c>DDBRegisterProcess</c> of the process (INFO, or TRACE when verbose) and never changes it, so a fixture that
/// must see DEBUG records (the cancel-at-a-log-record tests of XI-07) cannot rely on running before the fixtures that
/// register without it: this runs before all of them. The fixtures' own <c>RegisterProcess</c> calls then return at once.
/// </summary>
[SetUpFixture]
public class LibddbProcessSetup
{
    [OneTimeSetUp]
    public void RegisterLibddb()
    {
        UseDdbFolder();
        DdbAbi.Verify();
        new NativeDdbRuntime().RegisterProcess(verbose: true);
    }
}
