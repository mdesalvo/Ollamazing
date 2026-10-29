using Microsoft.AspNetCore.Components;

namespace Ollamazing.Web.Components.Shared;

/// <summary>Base for components that re-render when a shared singleton (status, catalog, downloads) raises a change event.</summary>
/// <remarks>Subscriptions are released on dispose, so a closed tab never keeps a singleton pointing to a dead circuit.</remarks>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    private readonly List<Action> unsubscriptions = [];

    /// <summary>Re-renders this component whenever the given event fires, until the component is disposed.</summary>
    /// <example><c>Watch(handler => Monitor.Changed += handler, handler => Monitor.Changed -= handler);</c></example>
    protected void Watch(Action<Action> subscribe, Action<Action> unsubscribe)
    {
        // Events fire on background threads, so the render is marshalled onto the circuit dispatcher.
        Action rerender = () => _ = InvokeAsync(StateHasChanged);
        subscribe(rerender);
        unsubscriptions.Add(() => unsubscribe(rerender));
    }

    public virtual void Dispose()
    {
        foreach (Action unsubscribe in unsubscriptions)
            unsubscribe();
        unsubscriptions.Clear();
        GC.SuppressFinalize(this);
    }
}
