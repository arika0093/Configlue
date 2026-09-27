using System.Diagnostics;

namespace Configlue;

public sealed partial class ConfiglueProfiledOptions<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private sealed class ActiveProfileValueSubscription : IDisposable
    {
        private readonly ConfiglueProfiledOptions<TModel, TFragment> _owner;
        private readonly Action<TModel> _listener;
        private readonly object _gate = new();
        private string? _profileName;
        private IWritableOptions<TModel>? _profile;
        private IDisposable? _profileSubscription;
        private bool _disposed;

        public ActiveProfileValueSubscription(
            ConfiglueProfiledOptions<TModel, TFragment> owner,
            Action<TModel> listener
        )
        {
            _owner = owner;
            _listener = listener;
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _owner.ActiveProfileChanged += OnActiveProfileChanged;
            }

            var profileName = _owner.GetActiveProfileNameAsync().GetAwaiter().GetResult();
            var profile = _owner.GetProfileAsync(profileName).GetAwaiter().GetResult();
            Bind(profileName, profile, notify: false);
        }

        public void Dispose()
        {
            IDisposable? profileSubscription;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                profileSubscription = _profileSubscription;
                _profileSubscription = null;
                _profile = null;
            }

            _owner.ActiveProfileChanged -= OnActiveProfileChanged;
            profileSubscription?.Dispose();
            _owner.RemoveSubscription(this);
        }

        private void OnActiveProfileChanged(string profileName)
        {
            try
            {
                var profile = _owner.GetProfileAsync(profileName).GetAwaiter().GetResult();
                Bind(profileName, profile, notify: true);
            }
            catch (Exception exception)
            {
                Trace.TraceError(
                    "Configlue active-profile value subscription failed: {0}",
                    exception
                );
            }
        }

        private void Bind(string profileName, IWritableOptions<TModel> profile, bool notify)
        {
            IDisposable? previousSubscription;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                if (
                    string.Equals(_profileName, profileName, StringComparison.Ordinal)
                    && ReferenceEquals(_profile, profile)
                )
                {
                    return;
                }

                previousSubscription = _profileSubscription;
                _profileName = profileName;
                _profile = profile;
                _profileSubscription = profile.OnChange(value =>
                    OnProfileValueChanged(profile, value)
                );
            }

            previousSubscription?.Dispose();
            if (notify)
            {
                NotifyListener(profile.GetValueAsync().AsTask().GetAwaiter().GetResult());
            }
        }

        private void OnProfileValueChanged(IWritableOptions<TModel> profile, TModel value)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_profile, profile))
                {
                    return;
                }
            }

            NotifyListener(value);
        }

        private void NotifyListener(TModel value)
        {
            try
            {
                _listener(value);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Configlue active-profile value listener failed: {0}", exception);
            }
        }
    }
}
