namespace ForeverScout.Tests;

/// <summary>UI-kontroller som kan kontrolleras utan att visa ett fönster.</summary>
public class UiTests
{
    private static void Sta(Action a)
    {
        Exception? err = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { err = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (err != null) throw new Xunit.Sdk.XunitException(err.ToString());
    }

    [Fact]
    public void InputBoxHasInnerTextMarginSoFirstLetterIsNotClipped()
    {
        Sta(() =>
        {
            using var form = new Form();
            var input = new InputBox(380);
            form.Controls.Add(input);
            _ = form.Handle;
            _ = input.Box.Handle;
            input.Value = "j0n550n";
            var (left, right) = input.TextMargins;
            Assert.True(left >= InputBox.TextMarginLogical, $"vänstermarginal {left}");
            Assert.True(right >= InputBox.TextMarginLogical, $"högermarginal {right}");
            // Första tecknet ritas efter marginalen och ingen horisontell rullning döljer det.
            var p = input.Box.GetPositionFromCharIndex(0);
            Assert.True(p.X >= InputBox.TextMarginLogical, $"första tecknet vid x={p.X}");
            Assert.Equal("j0n550n".Length, input.Box.SelectionStart); // markören efter texten, inte över första tecknet
        });
    }
}
