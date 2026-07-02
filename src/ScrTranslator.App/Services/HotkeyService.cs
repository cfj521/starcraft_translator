using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using ScrTranslator.App.Interop;
using ScrTranslator.Core.Settings;

namespace ScrTranslator.App.Services;

/// <summary>全局热键：借某个窗口的消息队列注册系统级热键，收到 WM_HOTKEY 派发到对应动作。
/// M4：改为按"功能名"管理（而非只按 Win32 id），支持从绑定表批量注册、单功能重绑（失败自动回滚）、
/// 恢复默认（内部先全注销再重注册，避免重绑过程中默认表内互相"临时占用"报假冲突）。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;

    // 派发用：Win32 热键 id → 动作。
    private readonly Dictionary<int, Action> _idToAction = new();
    // 管理用：功能名 → 当前 id / 当前绑定 / 动作（后两者跨重绑保留，供 Rebind/ResetToDefaults 复用）。
    private readonly Dictionary<string, int> _funcToId = new();
    private readonly Dictionary<string, HotkeyBinding> _funcToBinding = new();
    private readonly Dictionary<string, Action> _funcToAction = new();
    private int _nextId = 1;

    public HotkeyService(Window window)
    {
        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd)!;
        _source.AddHook(WndProc);
    }

    /// <summary>注册一个全局热键（不关联功能名，供不需要 Rebind 的一次性场景使用）；
    /// 成功返回 true，失败（如被占用）返回 false。</summary>
    public bool Register(uint modifiers, uint vk, Action action)
    {
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers, vk))
            return false;
        _idToAction[id] = action;
        return true;
    }

    /// <summary>按功能名注册（若该功能名此前已注册，先注销旧绑定再注册新绑定）。
    /// 成功返回 true 并记录 func→binding→action；失败返回 false（该功能名不会残留旧注册）。</summary>
    public bool Register(string func, HotkeyBinding binding, Action action)
    {
        UnregisterFunc(func, keepAction: false);
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, binding.Modifiers, binding.Vk))
            return false;
        _idToAction[id] = action;
        _funcToId[func] = id;
        _funcToBinding[func] = binding;
        _funcToAction[func] = action;
        return true;
    }

    /// <summary>按绑定表批量注册全部功能（bindings 的 key 需能在 actions 中找到同名动作，找不到的功能名跳过）。
    /// 返回每个功能名的注册结果，供调用方记日志。</summary>
    public IReadOnlyDictionary<string, bool> RegisterAll(
        IReadOnlyDictionary<string, HotkeyBinding> bindings,
        IReadOnlyDictionary<string, Action> actions)
    {
        var results = new Dictionary<string, bool>();
        foreach (var (func, binding) in bindings)
        {
            if (actions.TryGetValue(func, out var action))
                results[func] = Register(func, binding, action);
        }
        return results;
    }

    /// <summary>把某功能重绑到新组合键：注销旧绑定 → 注册新绑定；若新绑定注册失败（如被其它程序占用），
    /// 自动回滚到重绑前的绑定并返回 false（该功能不会变成"无热键可用"状态）。
    /// 该功能须此前已通过 Register/RegisterAll 注册过（否则找不到关联动作，直接返回 false）。</summary>
    public bool Rebind(string func, HotkeyBinding newBinding)
    {
        if (!_funcToAction.TryGetValue(func, out var action))
            return false;
        _funcToBinding.TryGetValue(func, out var oldBinding);

        UnregisterFunc(func, keepAction: true);

        int id = _nextId++;
        if (NativeMethods.RegisterHotKey(_hwnd, id, newBinding.Modifiers, newBinding.Vk))
        {
            _idToAction[id] = action;
            _funcToId[func] = id;
            _funcToBinding[func] = newBinding;
            return true;
        }

        // 回滚：重新注册重绑前的绑定，使该功能仍可用。
        if (oldBinding is not null)
        {
            int rollbackId = _nextId++;
            if (NativeMethods.RegisterHotKey(_hwnd, rollbackId, oldBinding.Modifiers, oldBinding.Vk))
            {
                _idToAction[rollbackId] = action;
                _funcToId[func] = rollbackId;
                _funcToBinding[func] = oldBinding;
            }
        }
        return false;
    }

    /// <summary>恢复为给定默认绑定表：先注销所有当前已注册功能（但保留其动作），再按默认表逐一重新注册。
    /// 先全注销再全注册，避免"重绑到 A 的默认键时，B 尚未让出该键"导致的假性冲突失败。
    /// 只对此前已注册过（有关联动作）的功能名生效，其余默认项跳过。</summary>
    public void ResetToDefaults(IReadOnlyDictionary<string, HotkeyBinding> defaults)
    {
        foreach (var func in _funcToId.Keys.ToList())
            UnregisterFunc(func, keepAction: true);

        foreach (var (func, binding) in defaults)
        {
            if (!_funcToAction.TryGetValue(func, out var action)) continue;
            int id = _nextId++;
            if (NativeMethods.RegisterHotKey(_hwnd, id, binding.Modifiers, binding.Vk))
            {
                _idToAction[id] = action;
                _funcToId[func] = id;
                _funcToBinding[func] = binding;
            }
        }
    }

    /// <summary>查询某功能当前生效的绑定；未注册过返回 null。</summary>
    public HotkeyBinding? CurrentBinding(string func) =>
        _funcToBinding.TryGetValue(func, out var b) ? b : null;

    private void UnregisterFunc(string func, bool keepAction)
    {
        if (_funcToId.TryGetValue(func, out var id))
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
            _idToAction.Remove(id);
            _funcToId.Remove(func);
            _funcToBinding.Remove(func);
        }
        if (!keepAction)
            _funcToAction.Remove(func);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _idToAction.TryGetValue((int)wParam, out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _idToAction.Keys)
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _idToAction.Clear();
        _funcToId.Clear();
        _funcToBinding.Clear();
        _funcToAction.Clear();
        _source.RemoveHook(WndProc);
    }
}
