using System;
using System.Collections.Generic;
using System.Text;

namespace SubReel.Launcher.App.ViewModels;

public sealed class SectionViewModel
{
    public string Title { get; }
    public string Icon { get; }
    public string Description { get; }

    public SectionViewModel(
        string title,
        string icon,
        string description)
    {
        Title = title;
        Icon = icon;
        Description = description;
    }
}