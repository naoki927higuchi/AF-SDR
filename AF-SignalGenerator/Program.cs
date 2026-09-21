using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("AF-SDR.Checks")]
namespace AfSignalGenerator;
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new GeneratorForm());
    }
}
