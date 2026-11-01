namespace FireAxe;

// Portable state is anchored to the application, never the working directory or user profile.
public static class AppDataPaths
{
    public static string DataDirectory => Path.Combine(AppContext.BaseDirectory, "data");
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string GameConsoleDirectory => Path.Combine(DataDirectory, "GameConsole");
    public static string CrashDirectory => Path.Combine(DataDirectory, "crashes");
}
