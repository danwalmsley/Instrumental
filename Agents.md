Prefer using the get_errors tool, before building.

The UI project uses AvaloniaUI, ensure your XAML is compatible.

Currently we dont need to maintain the .Browser and .iOS projects.

When defining mvvm properties use CommunityToolkit.Mvvm >= 8.4.0 and use partial properties
i.e.
[ObservableProperty]
public partial Type PropertyName {get; set;}

The app uses a ViewLocator so if a ViewModel is name MyViewModel then it will resolve a view called MyView.axaml

classes should be ordered, public internal, protected, private and by fields, ctors, properties commands, events, methods.

Bindings can not have logical conditions i.e. {Binding !IsConnecting && !IsUpdating} bindings to booleans can have ! to invert. i.e. {Binding !IsConnecting}

When defining resources for light or darktheme use themedictionaries
<ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light"></ResourceDictionary>
    <ResourceDictionary x:Key="Dark"></ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>