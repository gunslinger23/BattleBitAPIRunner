using System.Collections.ObjectModel;

namespace BBRAPIModules;

public abstract class APIModule
{
    public ReadOnlyCollection<RunnerServer> Servers => _servers.AsReadOnly();

    internal List<RunnerServer> _servers { get; private set; }

    public bool IsLoaded { get; internal set; }

    internal void SetServers(List<RunnerServer> servers)
    {
        this._servers = servers;
    }

    public void Unload()
    {
        this.IsLoaded = false;
        this._servers = null!;
    }

    public virtual void OnModulesLoaded() { } // sighs silently
    public virtual void OnModuleUnloading() { }
    public virtual void OnCreatingGameServerInstance(RunnerServer server) { }
}