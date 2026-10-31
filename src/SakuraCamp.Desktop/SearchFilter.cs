using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SakuraCamp.Desktop;

// Leave text input and IME composition on the input path; filter once after a short
// idle interval. Never replace cards while the IME is still choosing characters.
internal sealed class SearchFilter
{
    private readonly DispatcherTimer timer;
    private readonly Action apply;
    private bool composing;

    public SearchFilter(TextBox input, Action apply)
    {
        this.apply = apply;
        timer = new DispatcherTimer(DispatcherPriority.Background, input.Dispatcher)
        { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Flush();
        input.TextChanged += (_, _) => Schedule();
        TextCompositionManager.AddPreviewTextInputStartHandler(input, (_, _) =>
        { composing = true; timer.Stop(); });
        TextCompositionManager.AddPreviewTextInputUpdateHandler(input, (_, _) =>
        { composing = true; timer.Stop(); });
        TextCompositionManager.AddPreviewTextInputHandler(input, (_, _) =>
        { composing = false; Schedule(); });
        input.LostKeyboardFocus += (_, _) => { composing = false; Schedule(); };
        input.Unloaded += (_, _) => timer.Stop();
        input.Loaded += (_, _) => Schedule();
    }

    private void Schedule()
    {
        timer.Stop();
        if (!composing) timer.Start();
    }

    internal void Flush()
    {
        timer.Stop();
        if (!composing) apply();
    }
}
