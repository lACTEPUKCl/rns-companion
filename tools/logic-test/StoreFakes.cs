namespace RnsCompanion.Services;
internal static class LogService
{
    public static string DataDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rns-store-tests-" + System.Guid.NewGuid());
    public static void Warn(string message) => System.Console.WriteLine(message);
}
