---
name: avalonia
description: "Convenciones para escribir y modificar la UI de RAF en Avalonia 11 con CommunityToolkit.Mvvm: vistas AXAML, viewmodels, bindings, estilos, colores, diálogos y el editor AvaloniaEdit. Usar al tocar cualquier archivo de src/Raf.Client o al mejorar el aspecto de la aplicación."
---

# Avalonia en RAF

Stack: Avalonia 11.3, `FluentTheme`, `CommunityToolkit.Mvvm`, AvaloniaEdit. Las versiones están en `Directory.Packages.props`. **No usar ReactiveUI ni `System.Reactive`.** Los textos de la UI van en español rioplatense.

Antes de agregar algo, copiar el patrón de una vista parecida. Si esta guía y la API instalada no coinciden, manda la API: compilar y corregir la guía.

## Dónde va cada cosa

| Qué | Dónde |
|---|---|
| Lógica sin UI (API, sesión, ejecución, sync) | `src/Raf.Client.Core`, con tests en `tests/Raf.Client.Core.Tests` |
| Viewmodels | `src/Raf.Client/ViewModels` |
| Vistas | `src/Raf.Client/Views` |
| Controles propios | `src/Raf.Client/Controls` |
| Dependencias y diálogos | `ClientServices` e `IDialogs`, en `Services/Services.cs` |
| Vista de cada viewmodel | `Application.DataTemplates`, en `App.axaml` |

Una vista nueva se muestra con un `DataTemplate` en `App.axaml` y un `ContentControl` enlazado al viewmodel. No hay contenedor de DI: las dependencias llegan por constructor.

## Viewmodels

```csharp
public sealed partial class EjemploViewModel(IDialogs dialogs) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PuedeEnviar))]
    private string? _nombre;

    public bool PuedeEnviar => !string.IsNullOrWhiteSpace(Nombre);

    [RelayCommand]
    private async Task GuardarAsync() { /* genera GuardarCommand */ }

    partial void OnNombreChanged(string? value) { /* reacción al cambio */ }
}
```

- Clases `partial`, y campos `_camelCase` con `[ObservableProperty]`.
- Las propiedades calculadas se avisan con `[NotifyPropertyChangedFor]`.
- Lo que llega desde otro hilo o evento de fondo pasa por `Dispatcher.UIThread.Post(...)`.
- Un viewmodel no abre ventanas: usa `IDialogs`. Los archivos se piden con `StorageProvider`, dentro de `WindowDialogs`. No usar `OpenFileDialog` ni `SaveFileDialog`, que están obsoletos.
- Los viewmodels exponen estado, no colores: un `IsSuccess` o un estado, y la vista decide el aspecto con clases.

## Vistas y bindings

Los compiled bindings están activos por defecto, así que falta `x:DataType` significa error de compilación.

- Todo `UserControl` o `Window` declara `x:DataType="vm:..."`.
- Cada `DataTemplate`, `TreeDataTemplate` o `Style` con bindings declara el suyo.
- No usar `{ReflectionBinding}` salvo que no haya alternativa, y explicar por qué.
- Se oculta con `IsVisible`; en Avalonia no existe `Visibility`.
- Negación y nulos: `{Binding !IsRunning}` y `{Binding Texto, Converter={x:Static ObjectConverters.IsNotNull}}`.
- Con más de un aspecto según el estado, usar una clase condicional en lugar de duplicar el control: `Classes.success="{Binding IsSuccess}"`.
- El code-behind queda para lo puramente visual, como `Tapped` en árboles o el foco.

## Estilos y colores

- **Dónde está cada cosa:** `Styles/Theme.axaml` tiene los colores de ambos temas, la tipografía y los radios; `Styles/Controls.axaml`, los estilos por clase; `Styles/Icons.axaml`, los iconos.
- Los estilos reutilizables van en clases de `Controls.axaml`; no repetir `FontSize`, colores o márgenes en cada elemento. Las más usadas:
  - Texto: `title`, `heading`, `caption`, `label`, `mono`, `error`, `warning`.
  - Botones: `accent` (acción principal), `link`, `icon` (solo icono, con `ToolTip`), `ghost` (icono y texto en barras), `danger`.
  - Superficies: `chrome`, `sidebar`, `surface`, `panel`, `card`, `group`, `dialog-footer`, `callout`.
  - Estado: `Border.chip` con `success`, `failure` o `warning`; `Ellipse.dirty`; `StackPanel.empty` para estados vacíos y `Border.kbd` para atajos.
  - Pestañas: `TabControl.documents` (requests abiertos) y `TabControl.section` (secciones compactas).
- **Iconos:** `<ctl:Icon Data="{StaticResource IconPlus}" />`. Toma el color de texto heredado. Un icono nuevo es una geometría de trazo en la grilla de 16×16 de `Icons.axaml`.
- **Métodos HTTP:** `<ctl:MethodBadge Method="{Binding Method}" />`; el color sale de los estilos.
- **Ningún color literal fuera de `Theme.axaml`**, ni en AXAML ni en C#. Usar `{DynamicResource ...}` para que respondan al cambio claro/oscuro; desde C#, `TryFindResource` con el `ActualThemeVariant` del control.
- Un color propio se define en `Theme.axaml` para ambas variantes:

```xml
<ResourceDictionary.ThemeDictionaries>
  <ResourceDictionary x:Key="Light">
    <SolidColorBrush x:Key="RafSuccessBrush" Color="..." />
  </ResourceDictionary>
  <ResourceDictionary x:Key="Dark">
    <SolidColorBrush x:Key="RafSuccessBrush" Color="..." />
  </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
```

- Revisar todo cambio visual en tema claro y en tema oscuro. El color nunca es la única señal: acompañarlo con texto o icono.
- `Panel` y `StackPanel` no tienen `Padding`: envolver en un `Border`, o usar `Margin`.

## Editor de texto

Para URLs, headers y bodies usar `ctl:TextField`, no `TextBox`: resalta `{{variables}}` y sintaxis.

- Se enlaza con `BoundText`; `Text` no admite binding.
- Multilínea con `Multiline="True"`; sintaxis con `Syntax` (`"Json"`, `"XML"`).
- `TextField` usa la clave de estilo de `TextEditor` y los temas ganan a los setters, por eso su aspecto se fija con valores locales en el constructor.

## Validar

1. `dotnet build`: detecta los errores de bindings compilados.
2. `dotnet test`, si cambió lógica. Lo testeable va en `Raf.Client.Core`.
3. Abrir la aplicación con `dotnet run --project src/Raf.Client` y probar la pantalla tocada, en ambos temas: la app arranca en oscuro y `RAF_THEME=light` fuerza el claro (`RAF_THEME=system` sigue al sistema). El build no valida el aspecto; si no se pudo mirar, decirlo.
