using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using TankDestroyer.API;
using TankDestroyer.Engine;

namespace TankDestroyer.ConsoleApp;

class Program
{
    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        SetCursorVisibility(false);

        try
        {
            ClearConsole();

            var config = LoadConfig();
            var botFolder = ResolvePath(config.BotFolder, "..\\Bots");
            var mapFolder = ResolvePath(config.MapFolder, "..\\Maps");

            if (!Directory.Exists(botFolder))
            {
                Console.WriteLine($"Bot folder not found: {botFolder}");
                return;
            }

            if (!Directory.Exists(mapFolder))
            {
                Console.WriteLine($"Map folder not found: {mapFolder}");
                return;
            }

            var botTypes = CollectBotsServices.LoadBots(botFolder);
            if (botTypes.Length == 0)
            {
                Console.WriteLine($"No bots found in: {botFolder}");
                return;
            }

            var maps = CollectMapsService.LoadMaps(mapFolder);
            if (maps.Length == 0)
            {
                Console.WriteLine($"No maps found in: {mapFolder}");
                return;
            }

            var selectedMap = SelectMap(maps);
            var selectedBotTypes = SelectBots(botTypes, selectedMap.SpawnPoints.Length);

            var bots = selectedBotTypes
                .Select(type => (IPlayerBot)Activator.CreateInstance(type)!)
                .ToArray();

            var playerColors = new Dictionary<int, ConsoleColor>();
            var playerLabels = new Dictionary<int, string>();
            for (var i = 0; i < selectedBotTypes.Count; i++)
            {
                var attribute = selectedBotTypes[i].GetCustomAttribute<BotAttribute>();
                var color = attribute?.Color ?? "#808080";
                playerColors[i] = MapHexToConsoleColor(color);
                playerLabels[i] = attribute?.Name ?? selectedBotTypes[i].Name;
            }

            var runner = new GameRunner(selectedMap, bots);
            var renderer = new ConsoleRenderer();

            renderer.Render(runner.GetTurns().Last(), selectedMap, playerColors, playerLabels);
            Thread.Sleep(1000);

            while (!runner.Finished)
            {
                runner.DoTurn();
                var lastTurn = runner.GetTurns().Last();
                renderer.Render(lastTurn, selectedMap, playerColors, playerLabels);
                Thread.Sleep(1000);
            }

            Console.WriteLine("Game Finished!");
        }
        finally
        {
            Console.ResetColor();
            SetCursorVisibility(true);
        }
    }

    private static AppConfig LoadConfig()
    {
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        if (!File.Exists(configPath))
        {
            return new AppConfig();
        }

        var json = File.ReadAllText(configPath);
        return System.Text.Json.JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
    }

    private static string ResolvePath(string? configuredPath, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(configuredPath) ? fallback : configuredPath;
        return Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(value);
    }

    private static World SelectMap(IReadOnlyList<World> maps)
    {
        Console.WriteLine("Select map:");
        for (var i = 0; i < maps.Count; i++)
        {
            var map = maps[i];
            Console.WriteLine($"{i + 1}. {map.Name} ({map.Width}x{map.Height}) spawns:{map.SpawnPoints.Length}");
        }

        while (true)
        {
            Console.Write("Map number: ");
            var input = Console.ReadLine();
            if (int.TryParse(input, out var number) && number >= 1 && number <= maps.Count)
            {
                return maps[number - 1];
            }

            Console.WriteLine("Invalid map selection.");
        }
    }

    private static List<Type> SelectBots(IReadOnlyList<Type> botTypes, int maxBots)
    {
        Console.WriteLine();
        Console.WriteLine($"Select bots (comma-separated indexes, max {maxBots}):");
        for (var i = 0; i < botTypes.Count; i++)
        {
            var type = botTypes[i];
            var attribute = type.GetCustomAttribute<BotAttribute>();
            var name = attribute?.Name ?? type.Name;
            var creator = attribute?.Creator ?? "Unknown";
            var color = attribute?.Color ?? "#808080";
            Console.WriteLine($"{i + 1}. {name} by {creator} [{color}]");
        }

        while (true)
        {
            Console.Write("Bot numbers: ");
            var input = Console.ReadLine() ?? string.Empty;
            var parts = input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var selectedIndexes = new List<int>();
            var valid = true;
            foreach (var part in parts)
            {
                if (!int.TryParse(part, out var number) || number < 1 || number > botTypes.Count)
                {
                    valid = false;
                    break;
                }

                var index = number - 1;
                if (!selectedIndexes.Contains(index))
                {
                    selectedIndexes.Add(index);
                }
            }

            if (!valid || selectedIndexes.Count == 0)
            {
                Console.WriteLine("Invalid bot selection.");
                continue;
            }

            if (selectedIndexes.Count > maxBots)
            {
                Console.WriteLine($"Too many bots selected. This map supports {maxBots}.");
                continue;
            }

            return selectedIndexes.Select(index => botTypes[index]).ToList();
        }
    }

    private static ConsoleColor MapHexToConsoleColor(string color)
    {
        var hex = color.Trim().TrimStart('#');
        if (hex.Length != 6)
        {
            return ConsoleColor.Gray;
        }

        if (!int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) ||
            !int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) ||
            !int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return ConsoleColor.Gray;
        }

        var palette = new Dictionary<ConsoleColor, (int R, int G, int B)>
        {
            { ConsoleColor.Black, (0, 0, 0) },
            { ConsoleColor.DarkBlue, (0, 0, 128) },
            { ConsoleColor.DarkGreen, (0, 128, 0) },
            { ConsoleColor.DarkCyan, (0, 128, 128) },
            { ConsoleColor.DarkRed, (128, 0, 0) },
            { ConsoleColor.DarkMagenta, (128, 0, 128) },
            { ConsoleColor.DarkYellow, (128, 128, 0) },
            { ConsoleColor.Gray, (192, 192, 192) },
            { ConsoleColor.DarkGray, (128, 128, 128) },
            { ConsoleColor.Blue, (0, 0, 255) },
            { ConsoleColor.Green, (0, 255, 0) },
            { ConsoleColor.Cyan, (0, 255, 255) },
            { ConsoleColor.Red, (255, 0, 0) },
            { ConsoleColor.Magenta, (255, 0, 255) },
            { ConsoleColor.Yellow, (255, 255, 0) },
            { ConsoleColor.White, (255, 255, 255) }
        };

        return palette
            .OrderBy(entry => SquaredDistance((r, g, b), entry.Value))
            .First()
            .Key;
    }

    private static int SquaredDistance((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var dr = a.R - b.R;
        var dg = a.G - b.G;
        var db = a.B - b.B;
        return dr * dr + dg * dg + db * db;
    }

    private static void SetCursorVisibility(bool visible)
    {
        try
        {
            Console.CursorVisible = visible;
        }
        catch (IOException)
        {
            // Some hosts do not support cursor visibility changes.
        }
    }

    private static void ClearConsole()
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // Some hosts do not support full-screen clearing.
        }
    }

    private class AppConfig
    {
        public string BotFolder { get; set; } = "..\\Bots";
        public string MapFolder { get; set; } = "..\\Maps";
    }
}
