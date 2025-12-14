
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelixFormatter;

public static class HelixRepairTool
{
    //instead of safe-mode arg , for use when app crash on startup to bypass the binary settings ,can edit settings to their good values that worked like resolution/full screen...

    /// <summary>
    /// Checks command line args for --helix-export or --helix-import.
    /// Returns TRUE if a command was executed (meaning the app should exit).
    /// </summary>
    public static bool HandleConsoleArgs<T>(string[] args, string hlxPath, bool portable = true) where T : new()
    {
        if (args == null || args.Length == 0) return false;

        string command = args[0];
        // Optional: Allow overriding the path via 2nd arg
        string targetPath = args.Length > 1 ? args[1] : hlxPath;

        if (command == "--helix-export")
        {
            ExportJson<T>(targetPath, portable);
            return true; // Signal to Main() to exit
        }

        if (command == "--helix-import")
        {
            ImportJson<T>(targetPath, portable);
            return true; // Signal to Main() to exit
        }

        return false;
    }

    private static void ExportJson<T>(string hlxPath, bool portable) where T : new()
    {
        Console.WriteLine($"[Helix] Loading Binary: {hlxPath}");
        if (!File.Exists(hlxPath))
        {
            Console.WriteLine("[Error] File not found.");
            return;
        }

        try
        {
            // 1. Load Helix
            T data = Helix.LoadOrNew<T>(hlxPath, portable);

            // 2. Convert to JSON (Pretty Printed)
            string jsonPath = hlxPath + ".json";
            string json = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = true,
                IncludeFields = true // Important for public fields
            });

            File.WriteAllText(jsonPath, json);
            Console.WriteLine($"[Success] Exported to: {jsonPath}");
            Console.WriteLine("Edit this file, then run with --helix-import to apply changes.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Export failed: {ex.Message}");
        }
    }

    private static void ImportJson<T>(string hlxPath, bool portable)
    {
        string jsonPath = hlxPath + ".json";
        Console.WriteLine($"[Helix] Importing JSON: {jsonPath}");

        if (!File.Exists(jsonPath))
        {
            Console.WriteLine("[Error] JSON file not found. Run --helix-export first.");
            return;
        }

        try
        {
            // 1. Read JSON
            string json = File.ReadAllText(jsonPath);
            T? data = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                IncludeFields = true
            });

            if (data == null) throw new Exception("JSON data was null");

            // 2. Save back to Helix
            Helix.Save(data, hlxPath, portable);
            Console.WriteLine($"[Success] Binary updated: {hlxPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] Import failed: {ex.Message}");
        }
    }
}


/*

use: internal Classes + InternalsVisibleTo (Compile-Time Defense)
   If your GameState class is public, anyone can reference Game.dll and create a new GameState().
   If you make it internal, their Console App cannot see the class to manipulate it. , 
Reflection (Type.GetType("Game.GameState")) can bypass it, but that is much harder 

[MessagePackObject]
   internal class GameState // <--- Changed from public
   {
       [Key(0)] public int Gold { get; set; }
   }
// Allow MessagePack's generated resolver to see your internal classes
   [assembly: InternalsVisibleTo("MessagePack.Resolvers.DynamicObjectResolver")]
   [assembly: InternalsVisibleTo("MessagePack.Generator")]

// Configure MessagePack to allow internal types
   var options = MessagePackSerializerOptions.Standard
       .WithResolver(MessagePack.Resolvers.StandardResolver.AllowPrivate);


you must restrict where it is used.
   1. The "Config Only" Pattern (Recommended)
   Allow users to repair settings.hlx (Resolution, Volume), but DO NOT add the code hook for save.hlx (Gold, XP).

 usage:
static void Main(string[] args)
   {
       // 1. Check if the user is trying to repair/convert files
        //Allow repairing settings (if resolution is broken, user can fix it)
       // If true, the tool runs and we return immediately.
       if (HelixRepairTool.HandleConsoleArgs<GameConfig>(args, "config.hlx", portable: false))
       {
           return; 
       }
//else:
   

   // DANGER: Do NOT add this line for your Save File if you don't want cheaters
   // if (HelixRepairTool.HandleConsoleArgs<GameState>(args, "save.hlx", portable: true)) return;

   // 2. Normal Application Start
   var settings = Helix.LoadOrNew<GameSettings>("config.hlx", portable: false);
   var save = Helix.LoadOrNew<GameState>("save.hlx", portable: true);
   
   RunGame(settings, save);
   }

Solution: Bind Key to Assembly Signature (Strong Naming)
   This is the strongest programmatic defense.
   You Sign your Game/App: In Visual Studio/Unity, you generate a Signing Key.
   Helix Checks the Signature: Helix derives the encryption key based on the Public Key Token of the executable.
 
 */