namespace MiniNet.Client.Cli;

public static class SetupWizard
{
    public static async Task<(string name, string switchName)> RunAsync(
        IReadOnlyList<string> switches, Func<string, Task> createSwitch)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("  What do you want to do?");
            Console.WriteLine("  1. Connect as a device");
            Console.WriteLine("  2. Create a new switch");
            Console.Write("  Choice: ");

            var choice = Console.ReadLine()?.Trim();

            if (choice == "2")
            {
                Console.Write("  Switch name: ");
                var swName = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrEmpty(swName)) { Console.WriteLine("  Name cannot be empty."); continue; }
                try
                {
                    await createSwitch(swName);
                    Console.WriteLine($"  Switch '{swName}' created.");
                    switches = [..switches, swName];
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  Failed to create switch: {ex.Message}");
                    Console.ResetColor();
                }
                continue;
            }

            if (choice != "1") { Console.WriteLine("  Enter 1 or 2."); continue; }

            // Pick a switch
            if (switches.Count == 0)
            {
                Console.WriteLine("  No switches available. Create one first (option 2).");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine("  Available switches:");
            for (var i = 0; i < switches.Count; i++)
                Console.WriteLine($"    {i + 1}. {switches[i]}");

            string selectedSwitch;
            while (true)
            {
                Console.Write($"  Select switch (1-{switches.Count}): ");
                var input = Console.ReadLine()?.Trim();
                if (int.TryParse(input, out var idx) && idx >= 1 && idx <= switches.Count)
                {
                    selectedSwitch = switches[idx - 1];
                    break;
                }
                Console.WriteLine("  Invalid choice.");
            }

            Console.Write("  Device name: ");
            var name = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) { Console.WriteLine("  Name cannot be empty."); continue; }

            return (name, selectedSwitch);
        }
    }
}
