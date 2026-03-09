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
    private RawInputHotkeyManager? _rawInputManager;

    public HotkeyController(
        Window ownerWindow,
        Func<AppLogger?> loggerAccessor,
        Func<Key, ModifierKeys, string> formatHotkey)
    {
        _ownerWindow = ownerWindow;
        _loggerAccessor = loggerAccessor;
        _formatHotkey = formatHotkey;
    }

    public bool TryRegisterBindings(IReadOnlyList<HotkeyBindingRegistration> bindings, bool useRawInputBackend)
    {
        if (!TryValidateBindings(bindings))
        {
            return false;
        }

        if (useRawInputBackend)
        {
            return TryRegisterBindingsRawInput(bindings);
        }

        return TryRegisterBindingsWin32(bindings);
    }

    public void Dispose()
    {
        foreach (var manager in _slots.Values)
        {
            manager.Dispose();
        }

        _slots.Clear();
        _rawInputManager?.Dispose();
        _rawInputManager = null;
    }

    private bool TryRegisterBindingsRawInput(IReadOnlyList<HotkeyBindingRegistration> bindings)
    {
        var candidate = new RawInputHotkeyManager(_ownerWindow);
        if (!candidate.TryRegisterBindings(bindings, out var reason))
        {
            _loggerAccessor()?.Error(
                $"Failed to register RawInput hotkeys. {reason ?? "unknown"}");
            candidate.Dispose();
            return false;
        }

        foreach (var manager in _slots.Values)
        {
            manager.Dispose();
        }

        _slots.Clear();
        _rawInputManager?.Dispose();
        _rawInputManager = candidate;
        return true;
    }

    private bool TryRegisterBindingsWin32(IReadOnlyList<HotkeyBindingRegistration> bindings)
    {
        var newSlots = new Dictionary<int, HotkeyManager>();
        foreach (var binding in bindings)
        {
            try
            {
                var manager = new HotkeyManager(_ownerWindow, binding.Key, binding.Modifiers, binding.Id);
                manager.HotkeyPressed += binding.Handler;
                manager.Register();
                newSlots.Add(binding.Id, manager);
            }
            catch (Exception ex)
            {
                foreach (var created in newSlots.Values)
                {
                    created.Dispose();
                }

                _loggerAccessor()?.Error(
                    $"Failed to register Win32 hotkeys. Failed to register hotkey ({binding.Name}: {_formatHotkey(binding.Key, binding.Modifiers)}). {ex.Message}");
                return false;
            }
        }

        _rawInputManager?.Dispose();
        _rawInputManager = null;
        foreach (var manager in _slots.Values)
        {
            manager.Dispose();
        }

        _slots.Clear();
        foreach (var pair in newSlots)
        {
            _slots.Add(pair.Key, pair.Value);
        }

        return true;
    }

    private bool TryValidateBindings(IReadOnlyList<HotkeyBindingRegistration> bindings)
    {
        var seen = new HashSet<(Key Key, ModifierKeys Modifiers)>();
        foreach (var binding in bindings)
        {
            if (!seen.Add((binding.Key, binding.Modifiers)))
            {
                _loggerAccessor()?.Error(
                    $"Failed to register hotkey ({binding.Name}: {_formatHotkey(binding.Key, binding.Modifiers)}). Duplicate binding in settings.");
                return false;
            }
        }

        return true;
    }
}

internal readonly record struct HotkeyBindingRegistration(
    string Name,
    Key Key,
    ModifierKeys Modifiers,
    int Id,
    EventHandler Handler);
