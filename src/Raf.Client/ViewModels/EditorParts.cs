using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Raf.Client.Core.Execution;
using Raf.Client.Core.Local;
using Raf.Domain.Requests;

namespace Raf.Client.ViewModels;

/// <summary>Parte de un editor que avisa cuando cambia su contenido.</summary>
public abstract class EditorPart : ObservableObject
{
    public event Action? Changed;

    protected void RaiseChanged() => Changed?.Invoke();
}

public sealed partial class KeyValueRowViewModel : ObservableObject
{
    private readonly KeyValueListViewModel _list;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _key;
    [ObservableProperty] private string? _value;

    public KeyValueRowViewModel(KeyValueListViewModel list, KeyValueItem item)
    {
        _list = list;
        _enabled = item.Enabled;
        _key = item.Key;
        _value = item.Value;
    }

    public bool CanEdit => _list.CanEdit;

    public bool IsReadOnly => !_list.CanEdit;

    public KeyValueItem ToItem() => new(Key ?? "", Value ?? "", Enabled);

    partial void OnEnabledChanged(bool value) => _list.NotifyChanged();

    partial void OnKeyChanged(string? value) => _list.NotifyChanged();

    partial void OnValueChanged(string? value) => _list.NotifyChanged();

    [RelayCommand]
    private void Remove() => _list.Remove(this);
}

public sealed partial class KeyValueListViewModel(bool canEdit) : EditorPart
{
    public bool CanEdit { get; } = canEdit;

    public ObservableCollection<KeyValueRowViewModel> Rows { get; } = [];

    public void Load(IEnumerable<KeyValueItem> items)
    {
        Rows.Clear();
        foreach (var item in items) Rows.Add(new KeyValueRowViewModel(this, item));
        RaiseChanged();
    }

    public List<KeyValueItem> ToItems() => Rows.Select(r => r.ToItem()).ToList();

    [RelayCommand]
    private void Add()
    {
        Rows.Add(new KeyValueRowViewModel(this, new KeyValueItem("", "")));
        RaiseChanged();
    }

    internal void Remove(KeyValueRowViewModel row)
    {
        Rows.Remove(row);
        RaiseChanged();
    }

    internal void NotifyChanged() => RaiseChanged();
}

public sealed partial class BodyEditorViewModel : EditorPart
{
    public static BodyType[] Types { get; } = Enum.GetValues<BodyType>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsForm), nameof(Syntax), nameof(CanFormat))]
    [NotifyCanExecuteChangedFor(nameof(FormatCommand))]
    private BodyType _type;

    [ObservableProperty] private string? _content;

    /// <summary>Resultado del último formateo; se borra con la próxima edición.</summary>
    [ObservableProperty] private string? _formatNotice;
    [ObservableProperty] private bool _formatFailed;

    private bool _formatting;

    public BodyEditorViewModel(bool canEdit)
    {
        CanEdit = canEdit;
        Form = new KeyValueListViewModel(canEdit);
        Form.Changed += RaiseChanged;
    }

    public BodyType[] AvailableTypes => Types;
    public bool CanEdit { get; }
    public bool IsReadOnly => !CanEdit;
    public KeyValueListViewModel Form { get; }
    public bool IsText => Type is BodyType.Json or BodyType.Text or BodyType.Xml;
    public bool IsForm => Type == BodyType.FormUrlEncoded;

    public string? Syntax => Type switch
    {
        BodyType.Json => "Json",
        BodyType.Xml => "XML",
        _ => null,
    };

    /// <summary>Formatear aplica a JSON y XML, y solo si se puede editar.</summary>
    public bool CanFormat => CanEdit && BodyFormatter.Supports(Type);

    public void Load(BodySpec body)
    {
        Type = body.Type;
        Content = body.Content;
        Form.Load(body.Form ?? []);
        FormatNotice = null;
    }

    public BodySpec ToSpec() => Type switch
    {
        BodyType.None => BodySpec.None,
        BodyType.FormUrlEncoded => new BodySpec(BodyType.FormUrlEncoded, Form: Form.ToItems()),
        var t => new BodySpec(t, Content ?? ""),
    };

    [RelayCommand(CanExecute = nameof(CanFormat))]
    private void Format()
    {
        var result = BodyFormatter.Format(Content ?? "", Type);
        if (!result.Succeeded)
        {
            FormatFailed = true;
            FormatNotice = result.Error;
            return;
        }

        FormatFailed = false;
        if (result.Text == Content)
        {
            FormatNotice = Texts.Body_AlreadyFormatted;
            return;
        }
        _formatting = true;
        Content = result.Text;
        _formatting = false;
        FormatNotice = Texts.Body_Formatted;
    }

    partial void OnTypeChanged(BodyType value)
    {
        FormatNotice = null;
        RaiseChanged();
    }

    partial void OnContentChanged(string? value)
    {
        if (!_formatting) FormatNotice = null;
        RaiseChanged();
    }
}

/// <summary>Editor de autenticación. En una App no se ofrece <see cref="AuthType.Inherit"/>.</summary>
public sealed partial class AuthEditorViewModel(bool canEdit, bool allowInherit = true) : EditorPart
{
    public static AuthType[] Types { get; } = Enum.GetValues<AuthType>();
    public static ApiKeyLocation[] Locations { get; } = Enum.GetValues<ApiKeyLocation>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBearer), nameof(IsBasic), nameof(IsApiKey), nameof(IsInherit))]
    private AuthType _type;

    [ObservableProperty] private string? _token;
    [ObservableProperty] private string? _username;
    [ObservableProperty] private string? _password;
    [ObservableProperty] private string? _apiKeyName;
    [ObservableProperty] private string? _apiKeyValue;
    [ObservableProperty] private ApiKeyLocation _apiKeyIn;

    public AuthType[] AvailableTypes { get; } = allowInherit ? Types : Types.Where(t => t != AuthType.Inherit).ToArray();
    public ApiKeyLocation[] AvailableLocations => Locations;
    public bool CanEdit { get; } = canEdit;
    public bool IsReadOnly => !CanEdit;
    public bool IsBearer => Type == AuthType.Bearer;
    public bool IsBasic => Type == AuthType.Basic;
    public bool IsApiKey => Type == AuthType.ApiKey;
    public bool IsInherit => Type == AuthType.Inherit;

    public void Load(AuthSpec auth)
    {
        Type = auth.Type;
        Token = auth.Token;
        Username = auth.Username;
        Password = auth.Password;
        ApiKeyName = auth.ApiKeyName;
        ApiKeyValue = auth.ApiKeyValue;
        ApiKeyIn = auth.ApiKeyIn;
    }

    public AuthSpec ToSpec() => Type switch
    {
        AuthType.None => AuthSpec.None,
        AuthType.Inherit => AuthSpec.Inherit,
        AuthType.Bearer => new AuthSpec(AuthType.Bearer, Token: Token ?? ""),
        AuthType.Basic => new AuthSpec(AuthType.Basic, Username: Username ?? "", Password: Password ?? ""),
        _ => new AuthSpec(AuthType.ApiKey, ApiKeyName: ApiKeyName ?? "", ApiKeyValue: ApiKeyValue ?? "", ApiKeyIn: ApiKeyIn),
    };

    partial void OnTypeChanged(AuthType value) => RaiseChanged();

    partial void OnTokenChanged(string? value) => RaiseChanged();

    partial void OnUsernameChanged(string? value) => RaiseChanged();

    partial void OnPasswordChanged(string? value) => RaiseChanged();

    partial void OnApiKeyNameChanged(string? value) => RaiseChanged();

    partial void OnApiKeyValueChanged(string? value) => RaiseChanged();

    partial void OnApiKeyInChanged(ApiKeyLocation value) => RaiseChanged();
}

public sealed partial class ExtractionRowViewModel : ObservableObject
{
    private readonly ExtractionListViewModel _list;

    [ObservableProperty] private bool _enabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameProblem))]
    private string? _variable;

    [ObservableProperty] private ExtractionSource _source;
    [ObservableProperty] private string? _path;

    public ExtractionRowViewModel(ExtractionListViewModel list, ResponseExtraction item)
    {
        _list = list;
        _enabled = item.Enabled;
        _variable = item.Variable;
        _source = item.Source;
        _path = item.Path;
    }

    public ExtractionSource[] Sources => ExtractionListViewModel.Sources;
    public bool CanEdit => _list.CanEdit;
    public bool IsReadOnly => !_list.CanEdit;

    /// <summary>Aviso si el nombre no sirve como <c>{{variable}}</c>; null si está bien o vacío.</summary>
    public string? NameProblem =>
        string.IsNullOrWhiteSpace(Variable) || SessionVariableStore.IsValidName(Variable.Trim())
            ? null
            : string.Format(CoreTexts.Session_InvalidName, Variable.Trim());

    public ResponseExtraction ToItem() => new((Variable ?? "").Trim(), Source, (Path ?? "").Trim(), Enabled);

    partial void OnEnabledChanged(bool value) => _list.NotifyChanged();
    partial void OnVariableChanged(string? value) => _list.NotifyChanged();
    partial void OnSourceChanged(ExtractionSource value) => _list.NotifyChanged();
    partial void OnPathChanged(string? value) => _list.NotifyChanged();

    [RelayCommand]
    private void Remove() => _list.Remove(this);
}

/// <summary>Las extracciones de un request: qué valores de la respuesta se guardan en variables de sesión.</summary>
public sealed partial class ExtractionListViewModel(bool canEdit) : EditorPart
{
    public static ExtractionSource[] Sources { get; } = Enum.GetValues<ExtractionSource>();

    public bool CanEdit { get; } = canEdit;

    public ObservableCollection<ExtractionRowViewModel> Rows { get; } = [];

    public void Load(IEnumerable<ResponseExtraction>? items)
    {
        Rows.Clear();
        foreach (var item in items ?? []) Rows.Add(new ExtractionRowViewModel(this, item));
        RaiseChanged();
    }

    /// <summary>null si no hay ninguna, para no cambiar los requests que nunca las usaron.</summary>
    public List<ResponseExtraction>? ToItems() => Rows.Count == 0 ? null : Rows.Select(r => r.ToItem()).ToList();

    [RelayCommand]
    private void Add()
    {
        Rows.Add(new ExtractionRowViewModel(this, new ResponseExtraction("", ExtractionSource.Body, "$.")));
        RaiseChanged();
    }

    internal void Remove(ExtractionRowViewModel row)
    {
        Rows.Remove(row);
        RaiseChanged();
    }

    internal void NotifyChanged() => RaiseChanged();
}

public enum SampleRowState { Inherited, Overridden, Removed, Added }

/// <summary>Una clave de headers o query en un sample: heredada, sobreescrita, quitada o propia.</summary>
public sealed partial class SampleKeyValueRowViewModel : ObservableObject
{
    private readonly SampleKeyValueListViewModel _list;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherited), nameof(IsOverridden), nameof(IsRemoved), nameof(IsAdded),
        nameof(StateText), nameof(CanOverride), nameof(CanRemove), nameof(CanInherit), nameof(CanEditValue), nameof(ValueReadOnly))]
    private SampleRowState _state;

    [ObservableProperty] private string? _key;
    [ObservableProperty] private string? _value;
    [ObservableProperty] private bool _enabled;

    public SampleKeyValueRowViewModel(
        SampleKeyValueListViewModel list, SampleRowState state, string key, string value, bool enabled,
        KeyValueItem? inherited)
    {
        _list = list;
        _state = state;
        _key = key;
        _value = value;
        _enabled = enabled;
        Inherited = inherited;
    }

    public KeyValueItem? Inherited { get; set; }

    public string InheritedText => Inherited is null ? ""
        : string.Format(Inherited.Enabled ? Texts.Sample_Inherited : Texts.Sample_InheritedDisabled, Inherited.Value);
    public bool HasInherited => Inherited is not null;
    public bool IsInherited => State == SampleRowState.Inherited;
    public bool IsOverridden => State == SampleRowState.Overridden;
    public bool IsRemoved => State == SampleRowState.Removed;
    public bool IsAdded => State == SampleRowState.Added;

    public string StateText => State switch
    {
        SampleRowState.Inherited => Texts.Sample_StateInherited,
        SampleRowState.Overridden => Texts.Sample_StateOverridden,
        SampleRowState.Removed => Texts.Sample_StateRemoved,
        _ => Texts.Sample_StateOwn,
    };

    public bool CanEdit => _list.CanEdit;
    public bool KeyReadOnly => !(CanEdit && IsAdded);
    public bool CanEditValue => CanEdit && State is SampleRowState.Overridden or SampleRowState.Added;
    public bool ValueReadOnly => !CanEditValue;
    public bool CanOverride => CanEdit && State is SampleRowState.Inherited or SampleRowState.Removed;
    public bool CanRemove => CanEdit && State is SampleRowState.Inherited or SampleRowState.Overridden;
    public bool CanInherit => CanEdit && State is SampleRowState.Overridden or SampleRowState.Removed or SampleRowState.Added;

    partial void OnKeyChanged(string? value) => _list.NotifyChanged();

    partial void OnValueChanged(string? value) => _list.NotifyChanged();

    partial void OnEnabledChanged(bool value) => _list.NotifyChanged();

    [RelayCommand]
    private void Override()
    {
        if (Inherited is { } i)
        {
            Value = i.Value;
            Enabled = i.Enabled;
        }
        State = SampleRowState.Overridden;
        _list.NotifyChanged();
    }

    [RelayCommand]
    private void RemoveKey()
    {
        State = SampleRowState.Removed;
        _list.NotifyChanged();
    }

    /// <summary>Vuelve a heredar; una clave que el request no tiene, desaparece.</summary>
    [RelayCommand]
    private void Inherit()
    {
        if (Inherited is { } i)
        {
            Value = i.Value;
            Enabled = i.Enabled;
            State = SampleRowState.Inherited;
            _list.NotifyChanged();
        }
        else
        {
            _list.Drop(this);
        }
    }

    public KeyValueOverride? ToOverride() => State switch
    {
        SampleRowState.Removed => new KeyValueOverride(Key ?? "", Remove: true),
        SampleRowState.Overridden or SampleRowState.Added => new KeyValueOverride(Key ?? "", Value ?? "", Enabled),
        _ => null,
    };
}

public sealed partial class SampleKeyValueListViewModel(bool canEdit, StringComparer keyComparer) : EditorPart
{
    public bool CanEdit { get; } = canEdit;

    public ObservableCollection<SampleKeyValueRowViewModel> Rows { get; } = [];

    /// <summary>Reconstruye las filas a partir de lo heredado y de los overrides.</summary>
    public void Load(IReadOnlyList<KeyValueItem> parent, IReadOnlyList<KeyValueOverride>? overrides)
    {
        Rows.Clear();
        var pending = (overrides ?? []).ToList();

        foreach (var item in parent.DistinctBy(i => i.Key, keyComparer))
        {
            var o = pending.FirstOrDefault(x => keyComparer.Equals(x.Key, item.Key));
            if (o is not null) pending.Remove(o);

            var state = o is null ? SampleRowState.Inherited : o.Remove ? SampleRowState.Removed : SampleRowState.Overridden;
            Rows.Add(new SampleKeyValueRowViewModel(
                this, state, item.Key, o?.Value ?? item.Value, o?.Enabled ?? item.Enabled, item));
        }

        foreach (var o in pending)
            Rows.Add(new SampleKeyValueRowViewModel(
                this, o.Remove ? SampleRowState.Removed : SampleRowState.Added, o.Key, o.Value ?? "", o.Enabled ?? true, null));

        RaiseChanged();
    }

    public List<KeyValueOverride>? ToOverrides()
    {
        var list = Rows.Where(r => !string.IsNullOrWhiteSpace(r.Key)).Select(r => r.ToOverride()).OfType<KeyValueOverride>().ToList();
        return list.Count == 0 ? null : list;
    }

    [RelayCommand]
    private void Add()
    {
        Rows.Add(new SampleKeyValueRowViewModel(this, SampleRowState.Added, "", "", true, null));
        RaiseChanged();
    }

    internal void Drop(SampleKeyValueRowViewModel row)
    {
        Rows.Remove(row);
        RaiseChanged();
    }

    internal void NotifyChanged() => RaiseChanged();
}
