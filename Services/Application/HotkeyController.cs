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

        if (bindings.Count == 0)
        {
            foreach (var manager in _slots.Values)
            {
                manager.Dispose();
            }

            _slots.Clear();
            _rawInputManager?.Dispose();
            _rawInputManager = null;
            return true;
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
        var retiredOldSlots = new Dictionary<int, HotkeyManager>(_slots);
        var changedBindings = new List<HotkeyBindingRegistration>();
        var failedCount = 0;

        foreach (var binding in bindings)
        {
            if (retiredOldSlots.TryGetValue(binding.Id, out var existing) &&
                existing.Key == binding.Key &&
                existing.Modifiers == binding.Modifiers)
            {
                // WHY: Reuse unchanged Win32 registrations so hotkey updates do not collide with the app's own existing bindings.
                newSlots.Add(binding.Id, existing);
                retiredOldSlots.Remove(binding.Id);
            }
            else
            {
                changedBindings.Add(binding);
            }
        }

        // WHY: Release every changed/retired binding first so swap updates like F8 <-> F10 do not fail against the app's own old registrations.
        foreach (var manager in retiredOldSlots.Values)
        {
            manager.Dispose();
        }

        foreach (var binding in changedBindings)
        {
            HotkeyManager? manager = null;
            try
            {
                manager = new HotkeyManager(_ownerWindow, binding.Key, binding.Modifiers, binding.Id);
                manager.HotkeyPressed += binding.Handler;
                manager.Register();
                newSlots.Add(binding.Id, manager);
            }
            catch (Exception ex)
            {
                manager?.Dispose();
                failedCount++;
                _loggerAccessor()?.Error(
                    $"Failed to register Win32 hotkeys. Failed to register hotkey ({binding.Name}: {_formatHotkey(binding.Key, binding.Modifiers)}). {ex.Message}");
            }
        }

        if (newSlots.Count == 0)
        {
            return false;
        }

        if (failedCount > 0)
        {
            // WHY: RegisterHotKey can fail because one combination is already owned by another app.
            // Keep the successfully registered bindings active instead of disabling the whole hotkey set.
            _loggerAccessor()?.Info(
                $"Win32 hotkeys registered with partial success. success={newSlots.Count} failed={failedCount}.");
        }

        _rawInputManager?.Dispose();
        _rawInputManager = null;
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
