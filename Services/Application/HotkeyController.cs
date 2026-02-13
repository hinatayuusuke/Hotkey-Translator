using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class HotkeyController : IDisposable
{
    private readonly Window _ownerWindow;
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Func<Key, ModifierKeys, string> _formatHotkey;
    private readonly Dictionary<int, HotkeyManager> _slots = new();

    public HotkeyController(
        Window ownerWindow,
        Func<AppLogger?> loggerAccessor,
        Func<Key, ModifierKeys, string> formatHotkey)
    {
        _ownerWindow = ownerWindow;
        _loggerAccessor = loggerAccessor;
        _formatHotkey = formatHotkey;
    }

    public bool TryRegisterBindings(IReadOnlyList<HotkeyBindingRegistration> bindings)
    {
        var allSucceeded = true;
        var seen = new HashSet<(Key Key, ModifierKeys Modifiers)>();
        foreach (var binding in bindings)
        {
            allSucceeded &= TryApplyHotkeyBinding(binding, seen);
        }

        return allSucceeded;
    }

    public void Dispose()
    {
        foreach (var manager in _slots.Values)
        {
            manager.Dispose();
        }

        _slots.Clear();
    }

    private bool TryApplyHotkeyBinding(
        HotkeyBindingRegistration binding,
        ISet<(Key Key, ModifierKeys Modifiers)> seen)
    {
        var tuple = (binding.Key, binding.Modifiers);
        if (!seen.Add(tuple))
        {
            _loggerAccessor()?.Error(
                $"Failed to register hotkey ({binding.Name}: {_formatHotkey(binding.Key, binding.Modifiers)}). Duplicate binding in settings.");
            return false;
        }

        if (_slots.TryGetValue(binding.Id, out var existing) &&
            existing.Key == binding.Key &&
            existing.Modifiers == binding.Modifiers)
        {
            return true;
        }

        HotkeyManager? previous = existing;
        if (existing != null)
        {
            _slots.Remove(binding.Id);
            existing.Dispose();
        }

        if (TryCreateHotkeyManager(binding.Key, binding.Modifiers, binding.Id, binding.Handler, out var manager, out var registerError))
        {
            if (manager == null)
            {
                return false;
            }

            _slots[binding.Id] = manager;
            return true;
        }

        _loggerAccessor()?.Error(
            $"Failed to register hotkey ({binding.Name}: {_formatHotkey(binding.Key, binding.Modifiers)}). {registerError?.Message}");
        if (previous == null)
        {
            return false;
        }

        // WHY: Failed updates should not disable unrelated operations; rollback keeps prior binding active.
        if (TryCreateHotkeyManager(previous.Key, previous.Modifiers, binding.Id, binding.Handler, out var restored, out var rollbackError))
        {
            if (restored == null)
            {
                return false;
            }

            _slots[binding.Id] = restored;
            _loggerAccessor()?.Info(
                $"Hotkey rollback applied for {binding.Name}: {_formatHotkey(previous.Key, previous.Modifiers)}.");
            return false;
        }

        _loggerAccessor()?.Error($"Failed to restore previous hotkey ({binding.Name}). {rollbackError?.Message}");
        return false;
    }

    private bool TryCreateHotkeyManager(
        Key key,
        ModifierKeys modifiers,
        int id,
        EventHandler handler,
        out HotkeyManager? manager,
        out Exception? error)
    {
        manager = null;
        error = null;
        try
        {
            var created = new HotkeyManager(_ownerWindow, key, modifiers, id);
            created.HotkeyPressed += handler;
            created.Register();
            manager = created;
            return true;
        }
        catch (Exception ex)
        {
            manager?.Dispose();
            manager = null;
            error = ex;
            return false;
        }
    }
}

internal readonly record struct HotkeyBindingRegistration(
    string Name,
    Key Key,
    ModifierKeys Modifiers,
    int Id,
    EventHandler Handler);
