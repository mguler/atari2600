using System;
using System.IO;
using System.Windows.Forms;

namespace Atari2600Emu;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        byte[] rom;

        if (args.Length > 0)
        {
            rom = File.ReadAllBytes(args[0]);
        }
        else
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Select an Atari 2600 ROM (.bin/.a26/.rom)",
                Filter = "Atari 2600 ROM (*.bin;*.a26;*.rom)|*.bin;*.a26;*.rom|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (ofd.ShowDialog() != DialogResult.OK)
            {
                MessageBox.Show("No ROM selected. Exiting.");
                return;
            }

            rom = File.ReadAllBytes(ofd.FileName);
        }

        // Basic sanity: common sizes are 2KB/4KB/8KB/16KB etc.
        if (rom.Length < 2048)
        {
            MessageBox.Show($"ROM too small: {rom.Length} bytes");
            return;
        }

        var emu = new Atari2600(new Cartridge(rom));
        Application.Run(new MainForm(emu));
    }
}
