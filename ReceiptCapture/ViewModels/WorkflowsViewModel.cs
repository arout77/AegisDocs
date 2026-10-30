using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ReceiptCapture.Core;
using ReceiptCapture.Core.Compliance;
using ReceiptCapture.Workflows;

namespace ReceiptCapture.ViewModels;

/// <summary>One workflow as the editor shows it. Every change is saved straight away.</summary>
public sealed class WorkflowEditor : ObservableObject
{
    private readonly Action _changed;
    private readonly Func<Workflow, string?> _validateWatch;
    private readonly Action<string> _tell;

    public WorkflowEditor(Workflow model, Action changed, Func<Workflow, string?> validateWatch, Action<string> tell)
    {
        Model = model;
        _changed = changed;
        _validateWatch = validateWatch;
        _tell = tell;
    }

    public Workflow Model { get; }

    private void Set<T>(T current, T value, Action<T> assign, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        foreach (var n in names) OnPropertyChanged(n);
        _changed();
    }

    public string Name
    {
        get => Model.Name;
        set => Set(Model.Name, string.IsNullOrWhiteSpace(value) ? Model.Name : value.Trim(), v => Model.Name = v, nameof(Name));
    }

    public string WatchFolder
    {
        get => Model.WatchFolder;
        set => Set(Model.WatchFolder, value ?? "", v => Model.WatchFolder = v, nameof(WatchFolder), nameof(WatchFolderText), nameof(StateText));
    }
    public string WatchFolderText => string.IsNullOrWhiteSpace(Model.WatchFolder) ? "No folder chosen yet" : Model.WatchFolder;

    public string OutputFolder
    {
        get => Model.OutputFolder;
        set => Set(Model.OutputFolder, value ?? "", v => Model.OutputFolder = v, nameof(OutputFolder), nameof(OutputFolderText));
    }
    public string OutputFolderText => string.IsNullOrWhiteSpace(Model.OutputFolder) ? "No folder chosen yet" : Model.OutputFolder;

    public bool IncludeSubfolders
    {
        get => Model.IncludeSubfolders;
        set => Set(Model.IncludeSubfolders, value, v => Model.IncludeSubfolders = v, nameof(IncludeSubfolders));
    }

    public bool WatchEnabled
    {
        get => Model.WatchEnabled;
        set
        {
            if (value == Model.WatchEnabled) return;
            if (value)
            {
                var problem = _validateWatch(Model);
                if (problem != null)
                {
                    _tell(problem);
                    // The check box has already flipped on screen: put it back once this change has finished.
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(WatchEnabled))));
                    return;
                }
            }
            Model.WatchEnabled = value;
            OnPropertyChanged(nameof(WatchEnabled));
            OnPropertyChanged(nameof(StateText));
            _changed();
        }
    }

    public string StateText => Model.WatchEnabled ? "Watching" : "Off";

    public IReadOnlyList<ComplianceProfile> Profiles => ComplianceProfiles.All;

    public ComplianceProfile SelectedProfile
    {
        get => Profiles.FirstOrDefault(p => p.Name == Model.ProfileName) ?? ComplianceProfiles.Hipaa;
        set { if (value != null) Set(Model.ProfileName, value.Name, v => Model.ProfileName = v, nameof(SelectedProfile)); }
    }

    public string CustomTerms
    {
        get => Model.CustomTerms;
        set => Set(Model.CustomTerms, value ?? "", v => Model.CustomTerms = v, nameof(CustomTerms));
    }

    public bool CheckHiddenContent { get => Model.CheckHiddenContent; set => Set(Model.CheckHiddenContent, value, v => Model.CheckHiddenContent = v, nameof(CheckHiddenContent)); }
    public bool MakeCleanCopies { get => Model.MakeCleanCopies; set => Set(Model.MakeCleanCopies, value, v => Model.MakeCleanCopies = v, nameof(MakeCleanCopies)); }
    public bool SaveReports { get => Model.SaveReports; set => Set(Model.SaveReports, value, v => Model.SaveReports = v, nameof(SaveReports)); }

    public bool CleanProperties { get => Model.CleanProperties; set => Set(Model.CleanProperties, value, v => Model.CleanProperties = v, nameof(CleanProperties)); }
    public bool CleanComments { get => Model.CleanComments; set => Set(Model.CleanComments, value, v => Model.CleanComments = v, nameof(CleanComments)); }
    public bool CleanTrackedChanges { get => Model.CleanTrackedChanges; set => Set(Model.CleanTrackedChanges, value, v => Model.CleanTrackedChanges = v, nameof(CleanTrackedChanges)); }
    public bool CleanHiddenText { get => Model.CleanHiddenText; set => Set(Model.CleanHiddenText, value, v => Model.CleanHiddenText = v, nameof(CleanHiddenText)); }
    public bool CleanAttachments { get => Model.CleanAttachments; set => Set(Model.CleanAttachments, value, v => Model.CleanAttachments = v, nameof(CleanAttachments)); }
    public bool CleanPreviews { get => Model.CleanPreviews; set => Set(Model.CleanPreviews, value, v => Model.CleanPreviews = v, nameof(CleanPreviews)); }
    public bool CleanPictures { get => Model.CleanPictures; set => Set(Model.CleanPictures, value, v => Model.CleanPictures = v, nameof(CleanPictures)); }
    public bool KeepPdfSearchable { get => Model.KeepPdfSearchable; set => Set(Model.KeepPdfSearchable, value, v => Model.KeepPdfSearchable = v, nameof(KeepPdfSearchable)); }

    public void RefreshState() => OnPropertyChanged(nameof(StateText));
}

/// <summary>One line in the activity list.</summary>
public sealed record ActivityItem(DateTime When, string Workflow, string File, string Result, bool NeedsReview)
{
    public string Time => When.ToString("h:mm:ss tt");
}

/// <summary>The Workflows tab: save a recipe once, point it at a folder, and let it run on whatever arrives.</summary>
public sealed class WorkflowsViewModel : ObservableObject
{
    private const int MaxActivity = 300;

    private readonly WorkflowEngine _engine;
    private WorkflowEditor? _selected;
    private string _status = "Make a workflow, choose the folder to watch and where results go, then switch it on.";

    public WorkflowsViewModel(WorkflowEngine engine)
    {
        _engine = engine;

        foreach (var w in engine.Store.Workflows) Workflows.Add(Wrap(w));
        Selected = Workflows.FirstOrDefault();

        NewCommand = new RelayCommand(New);
        DuplicateCommand = new RelayCommand(Duplicate, () => Selected != null);
        DeleteCommand = new RelayCommand(Delete, () => Selected != null);
        ChooseWatchCommand = new RelayCommand(() => ChooseFolder("Choose the folder to watch", f => Selected!.WatchFolder = f), () => Selected != null);
        ChooseOutputCommand = new RelayCommand(() => ChooseFolder("Choose where the results should go", f => Selected!.OutputFolder = f), () => Selected != null);
        RunNowCommand = new RelayCommand(RunNow, () => Selected != null);
        ClearActivityCommand = new RelayCommand(() => Activity.Clear(), () => Activity.Count > 0);

        engine.FileDone += OnFileDone;
        engine.Notice += OnNotice;
        engine.QueueChanged += OnQueueChanged;
    }

    // Hooks the view fills in
    public Action<string>? ShowMessage { get; set; }
    public Func<string, bool>? Confirm { get; set; }
    public Func<string, string?>? PickFolder { get; set; }

    public ObservableCollection<WorkflowEditor> Workflows { get; } = new();
    public ObservableCollection<ActivityItem> Activity { get; } = new();
    public bool IsEmpty => Workflows.Count == 0;

    public WorkflowEditor? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            OnPropertyChanged(nameof(HasSelected));
        }
    }

    public bool HasSelected => Selected != null;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public ICommand NewCommand { get; }
    public ICommand DuplicateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ChooseWatchCommand { get; }
    public ICommand ChooseOutputCommand { get; }
    public ICommand RunNowCommand { get; }
    public ICommand ClearActivityCommand { get; }

    // ------------------------------------------------------------------
    private WorkflowEditor Wrap(Workflow w) => new(w, OnEdited, Validate, text => ShowMessage?.Invoke(text));

    private string? Validate(Workflow w) => w.ProblemWithFolders();

    private void OnEdited()
    {
        try { _engine.Store.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke("Couldn't save your workflows: " + ex.Message);
        }

        _engine.Sync(sweepNew: true);      // a folder just switched on is looked at once, for files already in it
        foreach (var w in Workflows) w.RefreshState();
    }

    private void New()
    {
        var w = new Workflow { Name = UniqueName("New workflow") };
        _engine.Store.Workflows.Add(w);
        var editor = Wrap(w);
        Workflows.Add(editor);
        Selected = editor;
        OnPropertyChanged(nameof(IsEmpty));
        OnEdited();
        Status = "Workflow added. Choose its folders on the right.";
    }

    private void Duplicate()
    {
        if (Selected == null) return;
        var copy = Selected.Model.Copy(UniqueName(Selected.Name + " copy"));
        _engine.Store.Workflows.Add(copy);
        var editor = Wrap(copy);
        Workflows.Add(editor);
        Selected = editor;
        OnPropertyChanged(nameof(IsEmpty));
        OnEdited();
        Status = "Copied. The copy is switched off until you turn it on.";
    }

    private void Delete()
    {
        if (Selected == null) return;
        if (Confirm?.Invoke($"Delete the workflow \"{Selected.Name}\"?\n\nFiles it already produced are not touched.") != true) return;

        var index = Workflows.IndexOf(Selected);
        var model = Selected.Model;
        _engine.Store.Workflows.Remove(model);
        _engine.Store.ForgetHandled(model.Id);
        Workflows.Remove(Selected);
        Selected = Workflows.Count == 0 ? null : Workflows[Math.Min(index, Workflows.Count - 1)];
        OnPropertyChanged(nameof(IsEmpty));
        OnEdited();
    }

    private string UniqueName(string wanted)
    {
        var name = wanted;
        for (int n = 2; Workflows.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{wanted} {n}";
        return name;
    }

    private void ChooseFolder(string title, Action<string> apply)
    {
        var folder = PickFolder?.Invoke(title);
        if (!string.IsNullOrWhiteSpace(folder)) apply(folder);
    }

    private void RunNow()
    {
        if (Selected == null) return;
        var problem = Selected.Model.ProblemWithFolders();
        if (problem != null) { ShowMessage?.Invoke(problem); return; }

        _engine.SweepFolder(Selected.Model.Id);
        Status = "Looking at the folder now. Files already checked (same content) are skipped.";
    }

    // ------------------------------------------------------------------ events from the engine (background threads)
    private void OnFileDone(WorkflowOutcome o) => OnUi(() =>
    {
        Activity.Insert(0, new ActivityItem(DateTime.Now, o.Workflow, o.File, o.Result, o.NeedsReview));
        while (Activity.Count > MaxActivity) Activity.RemoveAt(Activity.Count - 1);
        Status = $"{o.File}: {o.Result}";
    });

    private void OnNotice(string workflow, string text) => OnUi(() =>
    {
        Activity.Insert(0, new ActivityItem(DateTime.Now, workflow, "", text, false));
        Status = $"{workflow}: {text}";
    });

    private void OnQueueChanged() => OnUi(() =>
    {
        var waiting = _engine.QueueLength;
        if (waiting > 0) Status = $"{waiting} file(s) waiting to be checked…";
    });

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
