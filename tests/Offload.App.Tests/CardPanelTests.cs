using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Offload.App.Controls;

namespace Offload.App.Tests;

public sealed class CardPanelTests
{
    [Theory]
    [InlineData(0, 199)]
    [InlineData(1, 199)] // Body is the reported 0 x 195 rectangle.
    [InlineData(200, 0)]
    [InlineData(200, 4)]
    [InlineData(2, 5)]
    [InlineData(320, 199)]
    public void PaintBackground_DuringLayout_DoesNotThrow(int width, int height)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            error = Record.Exception(() =>
            {
                using var card = new CardPanel
                {
                    AutoSize = false,
                    Dock = DockStyle.None,
                    ClientSize = new Size(width, height)
                };
                Assert.Equal(new Size(width, height), card.ClientSize);
                using var bitmap = new Bitmap(320, 200);
                using var graphics = Graphics.FromImage(bitmap);
                using var args = new PaintEventArgs(graphics, new Rectangle(0, 0, 320, 200));
                var paint = typeof(CardPanel)
                    .GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Action<PaintEventArgs>>(card);

                foreach (var hero in new[] { false, true })
                {
                    card.Hero = hero;
                    paint(args);
                }
            });
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
    }
}
