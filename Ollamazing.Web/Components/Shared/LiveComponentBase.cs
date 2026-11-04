using Microsoft.AspNetCore.Components;

namespace Ollamazing.Web.Components.Shared;

/// <summary>Base for components that re-render when a shared singleton (status, catalog, downloads) raises a change event.</summary>
/// <remarks>Subscriptions are released on dispose, so a closed tab never keeps a singleton pointing to a dead circuit.</remarks>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    /// <summary>Detach actions of every subscription made through <see cref="Watch"/>, run on dispose.</summary>
    private readonly List<Action> unsubscriptions = [];

    /// <summary>Re-renders this component whenever the given event fires, until the component is disposed.</summary>
    /// <example><c>Watch(handler => Monitor.Changed += handler, handler => Monitor.Changed -= handler);</c></example>
    protected void Watch(Action<Action> subscribe, Action<Action> unsubscribe)
    {
        // Events fire on background threads, so the render is marshalled onto the circuit dispatcher.
        Action rerender = () => _ = InvokeAsync(StateHasChanged);

        // From now on every raised event re-renders the component.
        subscribe(rerender);

        // The detach action is remembered so dispose removes exactly this handler from the singleton.
        unsubscriptions.Add(() => unsubscribe(rerender));
    }

    /// <summary>Detaches every subscription so the singletons stop referencing this component.</summary>
    public virtual void Dispose()
    {
        // Each handler is removed from the singleton that holds it.
        foreach (Action unsubscribe in unsubscriptions)
            unsubscribe();

        // A second dispose finds nothing left to detach.
        unsubscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
