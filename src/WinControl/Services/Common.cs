using System.Text;

namespace WinControl.Services;

/// <summary>Configuração e log do usuário ficam em %APPDATA%\win-control, fora do repositório (têm a chave da lâmpada).</summary>
public static class AppPaths
{
    public static readonly string Dir =
        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "win-control")).FullName;

    public static string Lamp => Path.Combine(Dir, "lamp_config.json");
    public static string Conforto => Path.Combine(Dir, "conforto.json");
    public static string LogFile => Path.Combine(Dir, "win-control.log");
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                // recomeça do zero quando passa de 512 KB
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n", Encoding.UTF8);
            }
            catch { /* log nunca derruba o app */ }
        }
    }
}

/// <summary>Avisa a interface que algo mudou (lâmpada, telas, tema, cena).</summary>
public static class Bus
{
    public static event Action? Changed;

    public static void Notify()
    {
        try { Changed?.Invoke(); } catch (Exception e) { Log.Write($"erro ao notificar: {e.Message}"); }
    }
}
