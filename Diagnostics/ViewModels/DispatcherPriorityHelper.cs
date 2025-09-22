using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Threading;

namespace Diagnostics.ViewModels;

internal static class DispatcherPriorityHelper
{
    static DispatcherPriorityHelper()
    {
        OrderedPriorities = new[]
        {
            DispatcherPriority.SystemIdle,
            DispatcherPriority.ApplicationIdle,
            DispatcherPriority.ContextIdle,
            DispatcherPriority.Background,
            DispatcherPriority.Input,
            DispatcherPriority.Render,
            DispatcherPriority.Normal,
            DispatcherPriority.Send
        };
    }

    public static IReadOnlyList<DispatcherPriority> OrderedPriorities { get; }

    public static Color ToColor(DispatcherPriority priority)
    {
        if (priority == DispatcherPriority.SystemIdle)
        {
            return Colors.Gray;
        }

        if (priority == DispatcherPriority.ApplicationIdle)
        {
            return Colors.MediumSlateBlue;
        }

        if (priority == DispatcherPriority.ContextIdle)
        {
            return Colors.SandyBrown;
        }

        if (priority == DispatcherPriority.Background)
        {
            return Colors.MediumSeaGreen;
        }

        if (priority == DispatcherPriority.Input)
        {
            return Colors.DodgerBlue;
        }

        if (priority == DispatcherPriority.Render)
        {
            return Colors.LightSkyBlue;
        }

        if (priority == DispatcherPriority.Normal)
        {
            return Colors.Gold;
        }

        if (priority == DispatcherPriority.Send)
        {
            return Colors.OrangeRed;
        }

        return Colors.White;
    }
}
