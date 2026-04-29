using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aquila.Core.Models;
using Aquila.Core.Services;
using Aquila.Core.Views;

namespace Aquila.Core.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly UiConfig _config;
    private readonly AuthService _auth = ServiceLocator.Auth;

    // Единственное активное дочернее окно (настройки / моды / вход)
    private Window? _activeChild;

    // ── Встроенные панели (капсулы внутри главного окна) ─────────────────────

    private bool _isModsPanelOpen;
    public bool IsModsPanelOpen
    {
        get => _isModsPanelOpen;
        set { _isModsPanelOpen = value; OnPropertyChanged(); }
    }

    private bool _isSettingsPanelOpen;
    public bool IsSettingsPanelOpen
    {
        get => _isSettingsPanelOpen;
        set { _isSettingsPanelOpen = value; OnPropertyChanged(); }
    }

    private ModsWindowViewModel? _modsPanel;
    public ModsWindowViewModel? ModsPanel
    {
        get => _modsPanel;
        set { _modsPanel = value; OnPropertyChanged(); }
    }

    // Кэш ViewModel модов — создаётся один раз при первом открытии для текущего сервера
    private ModsWindowViewModel? _cachedModsPanel;

    private SettingsViewModel? _settingsPanel;
    public SettingsViewModel? SettingsPanel
    {
        get => _settingsPanel;
        set { _settingsPanel = value; OnPropertyChanged(); }
    }

    private LoginViewModel? _loginPanel;
    public LoginViewModel? LoginPanel
    {
        get => _loginPanel;
        set { _loginPanel = value; OnPropertyChanged(); }
    }

    private bool _isLoginPanelOpen;
    public bool IsLoginPanelOpen
    {
        get => _isLoginPanelOpen;
        set { _isLoginPanelOpen = value; OnPropertyChanged(); }
    }

    /// <summary>Закрывает все встроенные панели.</summary>
    public void CloseAllPanels()
    {
        IsModsPanelOpen = false;
        ModsPanel = null;
        IsSettingsPanelOpen = false;
        SettingsPanel = null;
        IsLoginPanelOpen = false;
        LoginPanel = null;
    }

    /// <summary>Закрывает текущее активное дочернее окно.</summary>
    private void CloseActiveChild()
    {
        _activeChild?.Close();
        _activeChild = null;
    }

    /// <summary>
    /// Открывает дочернее окно. Если уже открыто такое же — закрывает его (toggle).
    /// Если открыто другое — закрывает его и открывает новое.
    /// </summary>
    private bool OpenChild(Window newWindow, bool toggle = true)
    {
        // Toggle: то же окно — закрываем
        if (toggle && _activeChild?.GetType() == newWindow.GetType())
        {
            CloseActiveChild();
            return false;
        }

        CloseActiveChild();

        _activeChild = newWindow;
        newWindow.Closed += (_, _) =>
        {
            if (_activeChild == newWindow)
                _activeChild = null;
        };
        newWindow.Show();
        (System.Windows.Application.Current.MainWindow as Aquila.Core.Views.MainWindow)
            ?.RegisterChild(newWindow);
        return true;
    }

    public MainViewModel()
    {
        _config = UiConfigService.Load();
        LoadImages();
        LoadServers();

        User = new UserViewModel();
        // Загружаем статистику один раз при старте — после восстановления сессии
        _ = User.TryRestoreAsync().ContinueWith(_ =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                _ = RefreshAllStatsAsync()));

        // Периодическое обновление убрано — статистика загружается только при старте и после входа

        // Открытие окна входа через кнопку аватара
        User.OpenLoginRequested += () =>
        {
            if (IsLoginPanelOpen)
            {
                IsLoginPanelOpen = false;
                return;
            }
            IsModsPanelOpen = false; ModsPanel = null;
            IsSettingsPanelOpen = false; SettingsPanel = null;
            var vm = new LoginViewModel();
            vm.OnSuccess = session =>
            {
                User.ApplySession(session);
                _ = RefreshAllStatsAsync();
                IsLoginPanelOpen = false;
            };
            LoginPanel = vm;
            IsLoginPanelOpen = true;
        };

        OpenConfigCommand = new RelayCommand(_ =>
        {
            if (IsSettingsPanelOpen) { IsSettingsPanelOpen = false; return; }
            IsModsPanelOpen = false;
            IsLoginPanelOpen = false;
            var vm = new SettingsViewModel();
            vm.RequestClose = () => { IsSettingsPanelOpen = false; RefreshLauncherName(); };
            SettingsPanel = vm;
            IsSettingsPanelOpen = true;
        });

        OpenModsCommand = new RelayCommand(
            _ =>
            {
                if (SelectedServer == null) return;
                if (IsModsPanelOpen) { IsModsPanelOpen = false; return; }
                IsSettingsPanelOpen = false;
                IsLoginPanelOpen = false;
                // Используем кэшированный ViewModel — без пересоздания при каждом открытии
                ModsPanel = _cachedModsPanel ??= new ModsWindowViewModel(SelectedServer);
                IsModsPanelOpen = true;
            },
            _ => SelectedServer != null);

        PlayCommand = new RelayCommand(
            _ => _ = PlayAsync(),
            _ => SelectedServer != null && !IsSyncing && !IsGameRunning);

        KillGameCommand = new RelayCommand(_ =>
        {
            try { _gameProcess?.Kill(entireProcessTree: true); }
            catch (Exception ex) { AppLogger.Warn("KillGameCommand", ex); }
        });

        // Единая кнопка: ИГРАТЬ или ЗАКРЫТЬ
        PlayOrKillCommand = new RelayCommand(
            _ =>
            {
                if (IsGameRunning)
                {
                    _gameKilledByUser = true;
                    try { _gameProcess?.Kill(entireProcessTree: true); }
                    catch (Exception ex) { AppLogger.Warn("PlayOrKillCommand", ex); }
                }
                else
                {
                    _ = PlayAsync();
                }
            },
            _ => SelectedServer != null && !IsSyncing);

        // Перерисовываем кнопку ИГРАТЬ при смене состояния авторизации
        User.PropertyChanged += (_, _) => CommandManager.InvalidateRequerySuggested();
    }

    // ── Пользователь ─────────────────────────────────────────────────────────
    public UserViewModel User { get; }

    // ── Команды ──────────────────────────────────────────────────────────────

    public ICommand OpenConfigCommand { get; }
    public ICommand OpenModsCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand KillGameCommand { get; }
    public ICommand PlayOrKillCommand { get; }

    private async Task PlayAsync()
    {
        if (SelectedServer == null) return;

        // Закрываем все вспомогательные окна
        CloseActiveChild();

        // ── Авторизация ───────────────────────────────────────────────────────
        if (!User.IsLoggedIn)
        {
            // Открываем панель входа через тот же механизм что и кнопка "Войти"
            User.RequestLogin();

            // Ждём пока пользователь войдёт или закроет панель
            var tcs = new TaskCompletionSource<bool>();
            void OnClosed(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(IsLoginPanelOpen) && !IsLoginPanelOpen)
                {
                    PropertyChanged -= OnClosed;
                    tcs.TrySetResult(User.IsLoggedIn);
                }
            }
            PropertyChanged += OnClosed;

            var loggedIn = await tcs.Task;
            if (!loggedIn) return;
        }

        var session = User.Session!;

        if (session.IsExpired)
        {
            var refreshed = await _auth.RefreshAsync(session.RefreshToken);
            if (refreshed == null)
            {
                System.Windows.MessageBox.Show(
                    "Сессия истекла. Войдите снова.",
                    "Aquila", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                User.Session = null;
                return;
            }
            User.Session = refreshed;
            session = refreshed;
        }

        // ── Синхронизация модов ───────────────────────────────────────────────
        var serverConfig = await SyncModsAsync();
        if (serverConfig == null) return;

        // ── JoinServer (Yggdrasil) ────────────────────────────────────────────
        var serverId = Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(_auth.YggdrasilBaseUrl))
        {
            var (joined, joinError) = await _auth.JoinServerAsync(session, serverId);
            if (!joined)
            {
                var logPath = Path.Combine(LauncherSettingsService.LogsFolder, "launch.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] JoinServer failed: {joinError}\n");

                var result = System.Windows.MessageBox.Show(
                    $"Сервер авторизации недоступен:\n{joinError}\n\n" +
                    "Запустить игру без авторизации? (сервер может не пустить)",
                    "Aquila — предупреждение",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (result != System.Windows.MessageBoxResult.Yes)
                    return;
            }
        }

        // ── Регистрация игрового сеанса на моде ──────────────────────────────
        string? gameSessionKey = null;
        if (!string.IsNullOrWhiteSpace(serverConfig.AuthApiUrl))
        {
            gameSessionKey = await LauncherSessionService.CreateAsync(serverConfig.AuthApiUrl, session);
            if (gameSessionKey == null)
            {
                var result = System.Windows.MessageBox.Show(
                    "Не удалось зарегистрировать сеанс на сервере.\n" +
                    "Возможно, сервер временно недоступен.\n\n" +
                    "Запустить игру? (сервер может не пустить)",
                    "Aquila — предупреждение",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning);

                if (result != System.Windows.MessageBoxResult.Yes)
                    return;
            }
        }

        LaunchMinecraft(session, serverId, serverConfig, gameSessionKey);
    }

    // ── Синхронизация модов ───────────────────────────────────────────────────

    private bool _isSyncing;
    public bool IsSyncing
    {
        get => _isSyncing;
        private set { _isSyncing = value; OnPropertyChanged(); OnPropertyChanged(nameof(SyncStatusText)); CommandManager.InvalidateRequerySuggested(); }
    }

    private string _syncStatusText = "";
    public string SyncStatusText
    {
        get => _syncStatusText;
        private set { _syncStatusText = value; OnPropertyChanged(); }
    }

    private int _syncProgress;
    public int SyncProgress
    {
        get => _syncProgress;
        private set { _syncProgress = value; OnPropertyChanged(); }
    }

    private int _syncTotal;
    public int SyncTotal
    {
        get => _syncTotal;
        private set { _syncTotal = value; OnPropertyChanged(); }
    }

    // ── Состояние запущенной игры ─────────────────────────────────────────────

    private System.Diagnostics.Process? _gameProcess;
    private bool _gameKilledByUser;

    private bool _isGameRunning;
    public bool IsGameRunning
    {
        get => _isGameRunning;
        private set
        {
            _isGameRunning = value;
            OnPropertyChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task<ServerConfig?> SyncModsAsync()
    {
        if (SelectedServer == null) return null;

        var serverConfig = ServersConfigService.Load()
            .FirstOrDefault(s => s.Name == SelectedServer.Name);

        if (serverConfig == null) return null;

        // ── Определяем путь установки ядра ────────────────────────────────────
        var instancePath = LauncherSettingsService.GetServerFolder(serverConfig.Name);

        serverConfig.CorePath = instancePath;

        // Подгружаем актуальные моды из файла
        if (serverConfig.Mods.Count == 0)
            serverConfig.Mods = ModsConfigService.Load(serverConfig.Name);

        // ── Проверяем что Minecraft не запущен ────────────────────────────────
        // Проверяем только процесс запущенный этим лаунчером, а не любой java в системе
        if (_gameProcess != null && !_gameProcess.HasExited)
        {
            var result = System.Windows.MessageBox.Show(
                "Minecraft уже запущен. Для обновления модов его нужно закрыть.\n\nЗакрыть Minecraft и продолжить?",
                "Aquila", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                try { _gameProcess.Kill(entireProcessTree: true); _gameProcess.WaitForExit(3000); } catch { /* игнорируем */ }
            }
            else
            {
                return null;
            }
        }

        IsSyncing = true;
        SyncProgress = 0;
        SyncTotal = 1;

        try
        {
            // ── Устанавливаем Minecraft + модлоадер через официальный installer ──
            var versionService = new VersionDownloadService();
            await versionService.EnsureVersionsAsync(
                serverConfig,
                (msg, dl, tot) =>
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        SyncStatusText = msg;
                        SyncProgress   = (int)(tot > 0 ? dl * 100 / tot : 0);
                        SyncTotal      = 100;
                    });
                },
                ct: default);

            // ── Скачиваем библиотеки из version JSON-ов ────────────────────────
            var libService = new LibraryDownloadService();
            await libService.EnsureLibrariesAsync(
                (msg, cur, total) =>
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        SyncStatusText = msg;
                        SyncProgress   = total > 0 ? cur * 100 / total : 0;
                        SyncTotal      = 100;
                    });
                },
                ct: default);

            // ── Скачиваем ассеты (звуки, языки и др.) ──────────────────────────
            try
            {
                var assetService = new AssetDownloadService();
                await assetService.EnsureAssetsAsync(
                    serverConfig.MinecraftVersion,
                    (msg, cur, total) =>
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            SyncStatusText = msg;
                            SyncProgress   = total > 0 ? cur * 100 / total : 0;
                            SyncTotal      = 100;
                        });
                    },
                    ct: default);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception assetEx)
            {
                var logPath = Path.Combine(LauncherSettingsService.LogsFolder, "sync.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Ошибка ассетов: {assetEx}\n");
            }

            // ── Java runtime — скачиваем с Adoptium если нет ──────────────────
            try
            {
                var javaw = Path.Combine(LauncherSettingsService.GetRuntimeFolder(serverConfig.Name), "bin", "javaw.exe");
                if (!File.Exists(javaw))
                {
                    var runtimeService = new RuntimeDownloadService();
                    await runtimeService.EnsureRuntimeAsync(
                        instancePath,
                        serverConfig.Name,
                        (msg, dl, tot) =>
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                SyncStatusText = msg;
                                SyncProgress   = (int)(tot > 0 ? dl * 100 / tot : 0);
                                SyncTotal      = 100;
                            });
                        },
                        ct: default);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception runtimeEx)
            {
                var logPath = Path.Combine(LauncherSettingsService.LogsFolder, "sync.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] Ошибка runtime: {runtimeEx}\n");
            }

            // ── Синхронизируем моды ─────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(serverConfig.CorePath))
            {
                var enabledOptional = SelectedServer.GetEnabledOptional();
                var syncService = new ModSyncService();

                var logPath = Path.Combine(LauncherSettingsService.LogsFolder, "sync.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss}] Начало синхронизации. Локальных модов: {serverConfig.Mods.Count}\n");

                var updatedMods = await syncService.SyncAsync(
                    serverConfig,
                    enabledOptional,
                    (msg, cur, total) =>
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            SyncStatusText = msg;
                            SyncProgress   = cur;
                            SyncTotal      = total;
                        });
                    });

                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss}] Синхронизация завершена. Модов после синхронизации: {updatedMods.Count}\n");

                // Обновляем список модов в UI (не сохраняем в servers.json —
                // моды всегда берутся с удалённого манифеста)
                serverConfig.Mods = updatedMods;
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    SelectedServer?.ReloadMods(serverConfig);
                    // Сбрасываем кэш модов — список обновился после синхронизации
                    _cachedModsPanel = null;
                });
            }

            return serverConfig;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Ошибка синхронизации:\n{ex}",
                "Aquila", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return null;
        }
        finally
        {
            IsSyncing = false;
            SyncStatusText = "";
        }
    }

    /// <summary>
    /// Ищет javaw.exe: сначала в meta\runtimes\{server-name}\bin\javaw.exe,
    /// потом просто "javaw" из PATH.
    /// </summary>
    private static string ResolveJavaw(string serverName)
    {
        var bundled = Path.Combine(
            LauncherSettingsService.GetRuntimeFolder(serverName),
            "bin", "javaw.exe");
        if (File.Exists(bundled))
            return bundled;

        // Fallback — Java из PATH
        return "javaw";
    }

    private void LaunchMinecraft(AuthSession session, string serverId, ServerConfig serverConfig, string? gameSessionKey = null)
    {
        if (SelectedServer == null) return;

        var instancePath = LauncherSettingsService.GetServerFolder(serverConfig.Name);

        var logPath = Path.Combine(LauncherSettingsService.LogsFolder, "launch.log");

        try
        {
            // Проверяем что instance существует
            if (!Directory.Exists(instancePath))
            {
                System.Windows.MessageBox.Show(
                    $"Папка instance не найдена:\n{instancePath}\n\nСначала нужно скачать ядро.",
                    "Aquila", System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            var args = MinecraftLaunchArgs.Build(instancePath, session, LauncherSettingsService.Load(), serverConfig, serverId);

            // Ищем Java: сначала в meta\runtimes\{server-name}\, потом в PATH
            var javawPath = ResolveJavaw(serverConfig.Name);

            // Пишем лог для отладки
            File.WriteAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Запуск Minecraft\n" +
                $"Instance: {instancePath}\n" +
                $"Java: {javawPath}\n" +
                $"{javawPath} {args}\n");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName         = javawPath,
                Arguments        = args,
                UseShellExecute  = false,
                WorkingDirectory = instancePath,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
            };

            var process = new System.Diagnostics.Process { StartInfo = psi };

            // Открываем лог-файл один раз и держим его открытым всё время работы процесса.
            // File.AppendAllText не потокобезопасен — stdout и stderr приходят параллельно
            // и конкурируют за файл, что вызывает IOException "file is being used by another process".
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var logWriter = new StreamWriter(logPath, append: true, System.Text.Encoding.UTF8) { AutoFlush = true };
            var logLock   = new object();

            void WriteLog(string line)
            {
                lock (logLock) logWriter.WriteLine(line);
            }

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) WriteLog($"[OUT] {e.Data}");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) WriteLog($"[ERR] {e.Data}");
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Запоминаем процесс и переключаем кнопку
            _gameProcess = process;
            _gameKilledByUser = false;
            IsGameRunning = true;

            // Ждём завершения процесса асинхронно
            _ = Task.Run(async () =>
            {
                await process.WaitForExitAsync();

                // Закрываем лог-файл после завершения процесса
                lock (logLock) { logWriter.Flush(); logWriter.Dispose(); }

                // Сразу сбрасываем кнопку — не ждём сетевых запросов
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _gameProcess = null;
                    IsGameRunning = false;
                });

                // Удаляем сессию из БД когда игра закрылась (фоново, не блокируем UI)
                var currentSession = User.Session;
                if (currentSession != null)
                    await _auth.LeaveServerAsync(currentSession);

                // Отзываем игровой сеанс на моде
                if (currentSession != null &&
                    gameSessionKey != null &&
                    !string.IsNullOrWhiteSpace(serverConfig.AuthApiUrl))
                {
                    await LauncherSessionService.RevokeAsync(
                        serverConfig.AuthApiUrl, currentSession, gameSessionKey);
                }

                // Не показываем ошибку если игру закрыли мы сами через кнопку
                if (process.ExitCode != 0 && !_gameKilledByUser)
                {
                    await Task.Delay(300);
                    var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
                    var errLines = log.Split('\n')
                        .Where(l => l.StartsWith("[ERR]"))
                        .TakeLast(10)
                        .ToList();
                    var errText = errLines.Count > 0
                        ? string.Join("\n", errLines)
                        : $"Код выхода: {process.ExitCode}";

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        System.Windows.MessageBox.Show(
                            $"Minecraft завершился с ошибкой (код {process.ExitCode}):\n\n{errText}\n\nПолный лог: {logPath}",
                            "Aquila", System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Error));
                }
            });
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logPath, $"[EXCEPTION] {ex}\n"); } catch { }
            System.Windows.MessageBox.Show(
                $"Не удалось запустить Minecraft:\n{ex.Message}\n\nЛог: {logPath}",
                "Aquila", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    // ── Серверы ───────────────────────────────────────────────────────────────

    public ObservableCollection<ServerViewModel> Servers { get; } = [];

    private ServerViewModel? _selectedServer;
    public ServerViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (_selectedServer != null) _selectedServer.IsSelected = false;
            _selectedServer = value;
            if (_selectedServer != null) _selectedServer.IsSelected = true;
            OnPropertyChanged();
            ApplyServerBackground(_selectedServer);

            // Закрываем панель модов при переключении сервера
            if (_activeChild is Aquila.Core.Views.ModsWindow)
                CloseActiveChild();
            if (IsModsPanelOpen)
            {
                IsModsPanelOpen = false;
                ModsPanel = null;
            }
            // Сбрасываем кэш модов — у нового сервера свои моды
            _cachedModsPanel = null;
            // При переключении сервера — мгновенно показываем кэш, без сетевого запроса
            User?.ApplyStatsFromCache(_selectedServer?.Config);
        }
    }

    private void LoadServers()
    {
        var configs  = ServersConfigService.Load();
        var settings = LauncherSettingsService.Load();

        foreach (var c in configs)
        {
            c.Mods = ModsConfigService.Load(c.Name);
            settings.EnabledOptionalMods.TryGetValue(c.Name, out var savedEnabled);
            var enabledOptional = savedEnabled != null ? new HashSet<string>(savedEnabled) : null;
            Servers.Add(new ServerViewModel(c, enabledOptional));
        }

        SelectedServer = Servers.FirstOrDefault();
    }

    /// <summary>
    /// Загружает статистику для всех серверов параллельно и заполняет кэш.
    /// После загрузки применяет статистику текущего сервера.
    /// </summary>
    private async Task RefreshAllStatsAsync(CancellationToken ct = default)
    {
        var configs = ServersConfigService.Load()
            .Where(c => !string.IsNullOrWhiteSpace(c.StatsApiUrl))
            .ToList();

        // Загружаем все серверы параллельно
        var tasks = configs.Select(c => User.RefreshStatsAsync(c, ct)).ToList();
        await Task.WhenAll(tasks);

        // Применяем статистику текущего сервера из только что заполненного кэша
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
            User.ApplyStatsFromCache(SelectedServer?.Config));
    }

    // ── Фон (уровень 1) ──────────────────────────────────────────────────────

    private ImageSource? _backgroundImage;
    public ImageSource? BackgroundImage
    {
        get => _backgroundImage;
        set { _backgroundImage = value; OnPropertyChanged(); }
    }

    private double _backgroundDim;
    /// <summary>Затемнение основного фона 0.0–1.0</summary>
    public double BackgroundDim
    {
        get => _backgroundDim;
        set { _backgroundDim = Math.Clamp(value, 0, 1); OnPropertyChanged(); }
    }

    // ── Оверлей (уровень 2) ───────────────────────────────────────────────────

    private ImageSource? _overlayImage;
    /// <summary>Оверлей поверх фона — gif/png/jpg, без затемнения</summary>
    public ImageSource? OverlayImage
    {
        get => _overlayImage;
        set { _overlayImage = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOverlay)); }
    }

    public bool HasOverlay => _overlayImage != null;

    // ── Иконка окна ──────────────────────────────────────────────────────────

    private ImageSource? _windowIcon;
    /// <summary>Иконка окна и панели задач — .ico/.png из assets\</summary>
    public ImageSource? WindowIcon
    {
        get => _windowIcon;
        set { _windowIcon = value; OnPropertyChanged(); }
    }

    // ── Название лаунчера ────────────────────────────────────────────────────

    private string _launcherName = LauncherSettingsService.Load().LauncherName;
    public string LauncherName
    {
        get => _launcherName;
        private set { _launcherName = value; OnPropertyChanged(); }
    }

    public string LauncherVersion { get; } = UpdateConfigService.GetVersion();

    private void RefreshLauncherName()
    {
        LauncherSettingsService.InvalidateCache();
        LauncherName = LauncherSettingsService.Load().LauncherName;
    }

    // ── Загрузка ─────────────────────────────────────────────────────────────

    private void LoadImages()
    {
        // Загружаем глобальные значения из ui.json — они используются как fallback
        BackgroundDim = _config.BackgroundDim;

        if (!string.IsNullOrWhiteSpace(_config.BackgroundImage))
            BackgroundImage = TryLoadImage(_config.BackgroundImage);

        if (!string.IsNullOrWhiteSpace(_config.OverlayImage))
            OverlayImage = TryLoadImage(_config.OverlayImage);

        if (!string.IsNullOrWhiteSpace(_config.WindowIcon))
            WindowIcon = TryLoadImage(_config.WindowIcon);
    }

    /// <summary>
    /// Применяет фон выбранного сервера.
    /// Если у сервера не задано поле — используется глобальное значение из ui.json.
    /// </summary>
    private void ApplyServerBackground(ServerViewModel? server)
    {
        if (server == null)
        {
            // Нет выбранного сервера — возвращаем глобальный фон
            BackgroundDim = _config.BackgroundDim;
            BackgroundImage = string.IsNullOrWhiteSpace(_config.BackgroundImage)
                ? null : TryLoadImage(_config.BackgroundImage);
            OverlayImage = string.IsNullOrWhiteSpace(_config.OverlayImage)
                ? null : TryLoadImage(_config.OverlayImage);
            return;
        }

        // BackgroundDim: берём серверное если задано, иначе глобальное
        BackgroundDim = server.BackgroundDim ?? _config.BackgroundDim;

        // BackgroundImage: серверное → глобальное → null
        BackgroundImage = !string.IsNullOrWhiteSpace(server.BackgroundImage)
            ? TryLoadImage(server.BackgroundImage)
            : !string.IsNullOrWhiteSpace(_config.BackgroundImage)
                ? TryLoadImage(_config.BackgroundImage)
                : null;

        // OverlayImage: серверное → глобальное → null
        OverlayImage = !string.IsNullOrWhiteSpace(server.OverlayImage)
            ? TryLoadImage(server.OverlayImage)
            : !string.IsNullOrWhiteSpace(_config.OverlayImage)
                ? TryLoadImage(_config.OverlayImage)
                : null;
    }

    private static ImageSource? TryLoadImage(string fileName)
    {
        try
        {
            Uri uri;
            if (fileName.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                uri = new Uri(fileName);
            }
            else
            {
                // Единственное место — %APPDATA%\Aquila\assets\<имя файла>
                var fullPath = Path.Combine(
                    LauncherSettingsService.LauncherFolder,
                    "assets",
                    fileName);

                if (!File.Exists(fullPath)) return null;
                uri = new Uri(fullPath, UriKind.Absolute);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
