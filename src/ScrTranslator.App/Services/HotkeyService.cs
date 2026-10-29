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

    // 派发用：Win32 热键 id → 动作。仅含"当前生效中"的 OS 级注册。
    private readonly Dictionary<int, Action> _idToAction = new();
    // 管理用：功能名 → 当前生效 id。只有 RegisterHotKey 成功时才有条目；失败（如启动时被占用）则该 func 不在此表中。
    private readonly Dictionary<string, int> _funcToId = new();
    // 意图表：功能名 → 绑定 / 动作。只要调用过 Register(func, ...) 就会记录，且不随 RegisterHotKey 成败而消失，
    // 供 Rebind/ResetToDefaults 在此前注册失败时仍能找到动作并重新尝试。
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
    /// 无论 RegisterHotKey 是否成功，都会记录 func→binding→action 的"意图"（供 Rebind/ResetToDefaults 后续复用）；
    /// 只有 RegisterHotKey 成功时才会记录 func→id 的"当前生效"注册。
    /// 成功返回 true；失败返回 false（例如启动时被其它程序占用），但该功能名不会因此从管理表中消失。</summary>
    public bool Register(string func, HotkeyBinding binding, Action action)
    {
        UnregisterLive(func);
        _funcToAction[func] = action;
        _funcToBinding[func] = binding;

        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(_hwnd, id, binding.Modifiers, binding.Vk))
            return false;
        _idToAction[id] = action;
        _funcToId[func] = id;
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

    /// <summary>把某功能重绑到新组合键：注销旧绑定（若有生效中的）→ 注册新绑定；若新绑定注册失败（如被其它程序占用），
    /// 在此前确有生效绑定时自动回滚到重绑前的绑定，使该功能不会变成"无热键可用"状态。
    /// 即使该功能此前因启动时被占用而从未注册成功（无生效 id），只要曾通过 Register/RegisterAll 记录过意图
    /// （_funcToAction 中存在），本方法仍会尝试注册 newBinding——这正是"修改"能让此前失败的功能恢复的关键。
    /// 该功能须此前已通过 Register/RegisterAll 注册过意图（否则找不到关联动作，直接返回 false）。</summary>
    public bool Rebind(string func, HotkeyBinding newBinding)
    {
        if (!_funcToAction.TryGetValue(func, out var action))
            return false;
        _funcToBinding.TryGetValue(func, out var oldBinding);
        bool hadLive = _funcToId.ContainsKey(func);

        UnregisterLive(func);

        int id = _nextId++;
        if (NativeMethods.RegisterHotKey(_hwnd, id, newBinding.Modifiers, newBinding.Vk))
        {
            _idToAction[id] = action;
            _funcToId[func] = id;
            _funcToBinding[func] = newBinding;
            return true;
        }

        // 新绑定注册失败：仅当重绑前确有生效绑定时，才尝试恢复它（否则该功能本就无生效 id，无需也无从恢复）。
        if (hadLive && oldBinding is not null)
        {
            int rollbackId = _nextId++;
            if (NativeMethods.RegisterHotKey(_hwnd, rollbackId, oldBinding.Modifiers, oldBinding.Vk))
            {
                _idToAction[rollbackId] = action;
                _funcToId[func] = rollbackId;
                _funcToBinding[func] = oldBinding;
            }
            // 连旧绑定都无法恢复（理论上罕见）：_funcToBinding 保留 oldBinding 作为意图，_funcToId 无该 func 条目，
            // 不留下指向未注册 id 的残留映射。
        }
        // 重绑前本就无生效绑定：_funcToBinding 保持不变（仍是此前记录的意图，若从未记录过则维持缺省/null），
        // 不会残留孤立 id。
        return false;
    }

    /// <summary>恢复为给定默认绑定表：先注销所有当前生效中的功能，再按默认表逐一重新注册。
    /// 先全注销再全注册，避免"重绑到 A 的默认键时，B 尚未让出该键"导致的假性冲突失败。
    /// 只对此前记录过意图（_funcToAction 中存在，含启动时注册失败、当前无生效 id 的功能）的功能名生效，
    /// 其余默认项跳过——因此此前失败的功能也能在这里获得重新注册的机会。</summary>
    public void ResetToDefaults(IReadOnlyDictionary<string, HotkeyBinding> defaults)
    {
        foreach (var func in _funcToId.Keys.ToList())
            UnregisterLive(func);

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

    /// <summary>查询某功能记录的绑定意图（即使该绑定因启动时被占用等原因当前并未生效也会返回）；
    /// 从未 Register 过该功能名返回 null。</summary>
    public HotkeyBinding? CurrentBinding(string func) =>
        _funcToBinding.TryGetValue(func, out var b) ? b : null;

    /// <summary>某功能当前是否有生效中的 OS 级热键注册（区别于 CurrentBinding：后者只要记录过意图就非 null，
    /// 即使因启动时被占用而从未注册成功；本方法反映的是"此刻按下这个组合键是否真的会触发"）。</summary>
    public bool IsRegistered(string func) => _funcToId.ContainsKey(func);

    /// <summary>仅注销某功能"当前生效"的 OS 级注册（若有），不触碰 _funcToAction/_funcToBinding 记录的意图。
    /// 调用方负责在需要时另行更新意图表。</summary>
    private void UnregisterLive(string func)
    {
        if (_funcToId.TryGetValue(func, out var id))
        {
            // 注销失败（句柄已失效/该热键由其它线程注册）时 OS 侧仍然占着这个组合键：
            // 若照旧删掉映射，按下后 WM_HOTKEY 到达但查不到 action → 按键被吞、什么也不做，
            // 而且其它程序也注册不了。这里失败就保留映射，让原组合键仍然可用。
            if (!NativeMethods.UnregisterHotKey(_hwnd, id)) return;
            _idToAction.Remove(id);
            _funcToId.Remove(func);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _idToAction.TryGetValue((int)wParam, out var action))
        {
            // 热键在 UI 线程的消息泵里同步回调：action 里若做重活（校准窗构造、模型加载、
            // MessageBox）会让整个界面卡住、也不处理后续消息。这里只投递到消息队列，立刻返回。
            // 用 Dispatcher 而不是 Task.Run：action 大多要碰 UI（开窗口/改托盘），必须在 UI 线程跑。
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                {
                    try { action(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[hotkey] action 抛异常: {ex}"); }
                }));
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
