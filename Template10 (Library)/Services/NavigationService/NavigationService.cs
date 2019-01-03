﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Template10.Common;
using Template10.Services.LoggingService;
using Template10.Services.SerializationService;
using Template10.Services.ViewService;
using Template10.Utils;
using Windows.ApplicationModel.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using Portable = Template10.Mobile.Services.NavigationService;

namespace Template10.Services.NavigationService
{
    public partial class NavigationService : INavigationService
    {
        #region Debug

        internal static void DebugWrite(string text = null, LoggingService.Severities severity = Services.LoggingService.Severities.Template10, [CallerMemberName]string caller = null) =>
            Services.LoggingService.LoggingService.WriteLine(text, severity, caller: $"NavigationService.{caller}");

        #endregion

        public static INavigationService GetForFrame(Frame frame) =>
            WindowWrapper.ActiveWrappers.SelectMany(x => x.NavigationServices).FirstOrDefault(x => x.Frame.Equals(frame));

        private NavigationLogic Navigation { get; }

        private IViewService viewService = null;

        public BootStrapper.BackButton BackButtonHandling { get; set; }

        public SuspensionStateLogic Suspension { get; internal set; }

        public FrameFacade FrameFacade { get; private set; }

        public bool IsInMainView { get; }

        public object CurrentPageParam { get; internal set; }

        public Type CurrentPageType { get; internal set; }

        public IDispatcherWrapper Dispatcher => this.GetDispatcherWrapper();

        public ISerializationService SerializationService { get; set; } = Services.SerializationService.SerializationService.Json;

        [Obsolete("Use NavigationService.FrameFacade. This may be made private in future versions.", false)]
        public Frame Frame => FrameFacade.Frame;

        [Obsolete("Use FrameFacade.Content", true)]
        public object Content => FrameFacade.Content;

        [Obsolete("Use FrameFacade.Get/SetNavigationState()", true)]
        public string NavigationState { get { return FrameFacade.GetNavigationState(); } set { FrameFacade.SetNavigationState(value); } }

        protected internal NavigationService(Frame frame)
        {
            IsInMainView = CoreApplication.MainView == CoreApplication.GetCurrentView();
            FrameFacade = new FrameFacade(frame, this);
            Navigation = new NavigationLogic(this);
            Suspension = new SuspensionStateLogic(FrameFacade, this);
        }

        public Task<ViewLifetimeControl> OpenAsync(Type page, object parameter = null, string title = null, ViewSizePreference size = ViewSizePreference.UseHalf)
        {
            DebugWrite($"Page: {page}, Parameter: {parameter}, Title: {title}, Size: {size}");

            if (viewService == null) viewService = new ViewService.ViewService();

            return viewService.OpenAsync(page, parameter, title, size);
        }

        #region Navigate methods

        public async Task<bool> NavigateAsync(Type page, object parameter = null, NavigationTransitionInfo infoOverride = null)
        {
            DebugWrite($"Page: {page}, Parameter: {parameter}, NavigationTransitionInfo: {infoOverride}");

            // serialize parameter
            var serializedParameter = default(string);
            try
            {
                serializedParameter = SerializationService.Serialize(parameter);
            }
            catch
            {
                throw new Exception("Parameter cannot be serialized. See https://github.com/Windows-XAML/Template10/wiki/Page-Parameters");
            }

            return await NavigationOrchestratorAsync(page, parameter, NavigationMode.New, () =>
            {
                try
                {
                return FrameFacade.Navigate(page, serializedParameter, infoOverride);
                }
                catch (Exception ex)
                {
                    // Catch and ignore exceptions
                    DebugWrite(ex.Message, Severities.Error);
                    return false;
                }
            });
        }

        public void Navigate(Type page, object parameter = null, NavigationTransitionInfo infoOverride = null)
            => NavigateAsync(page, parameter, infoOverride).ConfigureAwait(true);

        /// <summary>
        /// Navigate<T> allows developers to navigate using a
        /// page key instead of the view type.This is accomplished by
        /// creating a custom Enum and setting up the PageKeys dict
        /// with the Key/Type pairs for your views.The dict is
        /// shared by all NavigationServices and is stored in
        /// the BootStrapper (or Application) of the app.
        /// 
        /// Implementation example:
        /// 
        /// // define your Enum
        /// public Enum Pages { MainPage, DetailPage }
        /// 
        /// // setup the keys dict
        /// var keys = BootStrapper.PageKeys<Views>();
        /// keys.Add(Pages.MainPage, typeof(Views.MainPage));
        /// keys.Add(Pages.DetailPage, typeof(Views.DetailPage));
        /// 
        /// // use Navigate<T>()
        /// NavigationService.Navigate(Pages.MainPage);
        /// </remarks>
        /// <typeparam name="T">T must be the same custom Enum used with BootStrapper.PageKeys()</typeparam>
        public async Task<bool> NavigateAsync<T>(T key, object parameter = null, NavigationTransitionInfo infoOverride = null)
            where T : struct, IConvertible
        {
            DebugWrite($"Key: {key}, Parameter: {parameter}, NavigationTransitionInfo: {infoOverride}");

            var keys = BootStrapper.Current.PageKeys<T>();
            if (!keys.TryGetValue(key, out Type page))
            {
                throw new KeyNotFoundException(key.ToString());
            }
            return await NavigateAsync(page, parameter, infoOverride).ConfigureAwait(true);
        }

        public void Navigate<T>(T key, object parameter = null, NavigationTransitionInfo infoOverride = null)
            where T : struct, IConvertible => NavigateAsync(key, parameter, infoOverride).ConfigureAwait(true);

        private async Task<bool> NavigationOrchestratorAsync(Type page, object parameter, NavigationMode mode, Func<bool> navigate)
        {
            DebugWrite($"Page: {page}, Parameter: {parameter}, NavigationMode: {mode}");

            if (page == null)
            {
                throw new ArgumentNullException(nameof(page));
            }
            if (navigate == null)
            {
                throw new ArgumentNullException(nameof(navigate));
            }

            // this cannot be used for duplicate navigation, except for refresh
            if ((mode != NavigationMode.Refresh)
                && (string.Equals(page.FullName, CurrentPageType?.FullName))
                && (ReferenceEquals(parameter, CurrentPageParam) || (parameter?.Equals(CurrentPageParam) ?? false)))
            {
                return false;
            }

            // fetch current (which will become old)
            var oldPage = FrameFacade.Content as Page;
            var oldViewModel = oldPage?.DataContext;

            // call oldViewModel.OnNavigatingFromAsync()
            var viewmodelCancels = await Navigation.NavingFromCancelsAsync(oldViewModel, oldPage, CurrentPageParam, false, mode, page, parameter);
            if (viewmodelCancels)
            {
                return false;
            }

            // raise Navigating event
            var eventCancels = RaiseNavigatingCancels(oldPage, parameter, false, mode.ToTemplate10NavigationMode(), page);
            if (eventCancels)
            {
                return false;
            }

            // invoke navigate (however custom)
            if (navigate.Invoke())
            {
                CurrentPageParam = parameter;
                CurrentPageType = page;
            }
            else
            {
                return false;
            }

            // fetch (current which is now new)
            var newPage = FrameFacade.Content as Page;
            var newViewModel = newPage?.DataContext;

            // raise Navigated event
            RaiseNavigated(newPage, parameter, mode.ToTemplate10NavigationMode());

            // call oldViewModel.OnNavigatedFrom()
            await Navigation.NavedFromAsync(oldViewModel, oldPage, false);

            // call newViewModel.ResolveForPage()
            if (newViewModel == null)
            {
                newViewModel = newPage.DataContext = BootStrapper.Current.ResolveForPage(newPage, this);
            }

            // call newTemplate10ViewModel.Properties
            if (newViewModel is ITemplate10ViewModel)
            {
                Navigation.SetupViewModel(this, newViewModel as ITemplate10ViewModel);
            }

            // call newViewModel.OnNavigatedToAsync()
            await Navigation.NavedToAsync(newViewModel, parameter, mode, newPage);

            // finally 
            return true;
        }

        #endregion

        #region events

        public event EventHandler<Portable.NavigatedEventArgs> Navigated;
        public void RaiseNavigated(Portable.NavigatedEventArgs e)
        {
            Navigated?.Invoke(this, e);
            // for backwards compat
            FrameFacade.RaiseNavigated(e);
        }
        public void RaiseNavigated(object page, object parameter, Portable.NavigationMode mode)
        {
            var navigatedEventArgs = new Portable.NavigatedEventArgs()
            {
                Page = page,
                Parameter = parameter,
                NavigationMode = mode,
                PageType = page?.GetType(),
            };
            RaiseNavigated(navigatedEventArgs);
        }

        public event EventHandler<Portable.NavigatingEventArgs> Navigating;
        public void RaiseNavigating(Portable.NavigatingEventArgs e)
        {
            Navigating?.Invoke(this, e);
            // for backwards compat
            FrameFacade.RaiseNavigating(e);
        }
        public bool RaiseNavigatingCancels(object page, object parameter, bool suspending, Portable.NavigationMode mode, Type targetType)
        {
            var navigatingDeferral = new Template10.Mobile.Common.DeferralManager();
            var navigatingEventArgs = new Portable.NavigatingEventArgs(navigatingDeferral)
            {
                Page = page,
                Parameter = parameter,
                Suspending = suspending,
                NavigationMode = mode,
                TargetPageType = targetType,
                TargetPageParameter = parameter,
            };
            RaiseNavigating(navigatingEventArgs);
            return navigatingEventArgs.Cancel;
        }

        public event EventHandler<HandledEventArgs> BackRequested;
        public void RaiseBackRequested(HandledEventArgs args)
        {
            BackRequested?.Invoke(this, args);
            // for backwards compat
            FrameFacade.RaiseBackRequested(args);
        }

        public event EventHandler<HandledEventArgs> ForwardRequested;
        public void RaiseForwardRequested(HandledEventArgs args)
        {
            ForwardRequested?.Invoke(this, args);
            // for backwards compat
            FrameFacade.RaiseForwardRequested(args);
        }

        public event EventHandler<CancelEventArgs<Type>> BeforeSavingNavigation;
        bool RaiseBeforeSavingNavigation()
        {
            var args = new CancelEventArgs<Type>(CurrentPageType);
            BeforeSavingNavigation?.Invoke(this, args);
            return args.Cancel;
        }

        public event TypedEventHandler<Type> AfterRestoreSavedNavigation;
        void RaiseAfterRestoreSavedNavigation() => AfterRestoreSavedNavigation?.Invoke(this, CurrentPageType);

        #endregion

        #region Save/Load Navigation methods

        public async Task SaveAsync()
        {
            // save navigation state into settings

            DebugWrite($"Frame: {FrameFacade.FrameId}");

            if (CurrentPageType == null)
                return;
            if (RaiseBeforeSavingNavigation())
                return;

            var frameState = Suspension.GetFrameState();
            if (frameState == null)
            {
                throw new InvalidOperationException("State container is unexpectedly null");
            }

            frameState.Write<string>("CurrentPageType", CurrentPageType.AssemblyQualifiedName);
            frameState.Write<object>("CurrentPageParam", CurrentPageParam);
            frameState.Write<string>("NavigateState", FrameFacade.GetNavigationState());

            await Task.CompletedTask;
        }

        public async Task<bool> LoadAsync()
        {
            // load navigation state from settings

            DebugWrite($"Frame: {FrameFacade.FrameId}");

            try
            {
                var frameState = Suspension.GetFrameState();
                if (frameState == null || !frameState.Exists("CurrentPageType"))
                {
                    return false;
                }

                CurrentPageType = frameState.Read<Type>("CurrentPageType");
                CurrentPageParam = frameState.Read<object>("CurrentPageParam");
                FrameFacade.SetNavigationState(frameState.Read<string>("NavigateState"));

                while (FrameFacade.Content == null)
                {
                    await Task.Delay(1);
                }

                var newPage = FrameFacade.Content as Page;
                var newViewModel = newPage?.DataContext;

                // newTemplate10ViewModel.Properties
                if (newViewModel is ITemplate10ViewModel)
                {
                    Navigation.SetupViewModel(this, newViewModel as ITemplate10ViewModel);
                }

                // newNavigatedAwareAsync.OnNavigatedTo
                await Navigation.NavedToAsync(newPage?.DataContext, CurrentPageParam, NavigationMode.Refresh, newPage);

                RaiseAfterRestoreSavedNavigation();
                return true;
            }
            catch { return false; }
        }

        #endregion

        #region Refresh methods

        public void Refresh() => RefreshAsync().ConfigureAwait(true);

        public void Refresh(object param) => RefreshAsync(param).ConfigureAwait(true);

        public async Task<bool> RefreshAsync()
        {
            return await NavigationOrchestratorAsync(CurrentPageType, CurrentPageParam, NavigationMode.Refresh, () =>
            {
                try
                {
                Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView().Reset();
                FrameFacade.SetNavigationState(FrameFacade.GetNavigationState());
                return true;
                }
                catch (Exception ex)
                {
                    // Catch and ignore exceptions
                    DebugWrite(ex.Message, Severities.Error);
                    return false;
                }
            });
        }

        public async Task<bool> RefreshAsync(object param)
        {
            return await NavigationOrchestratorAsync(CurrentPageType, param, NavigationMode.Refresh, () =>
            {
                try
                {
                Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView().Reset();
                FrameFacade.SetNavigationState(FrameFacade.GetNavigationState());
                return true;
                }
                catch (Exception ex)
                {
                    // Catch and ignore exceptions
                    DebugWrite(ex.Message, Severities.Error);
                    return false;
                }
            });
        }

        #endregion

        #region GoBack methods

        public bool CanGoBack => FrameFacade.CanGoBack;

        public void GoBack(NavigationTransitionInfo infoOverride = null) => GoBackAsync(infoOverride).ConfigureAwait(true);

        public async Task<bool> GoBackAsync(NavigationTransitionInfo infoOverride = null)
        {
            if (!CanGoBack)
            {
                return false;
            }
            var previous = FrameFacade.BackStack.LastOrDefault();
            var parameter = SerializationService.Deserialize(previous.Parameter?.ToString());
            return await NavigationOrchestratorAsync(previous.SourcePageType, parameter, NavigationMode.Back, () =>
            {
                try
                {
                FrameFacade.GoBack(infoOverride);
                return true;
                }
                catch (Exception ex)
                {
                    // Catch and ignore exceptions
                    DebugWrite(ex.Message, Severities.Error);
                    return false;
                }
            });
        }

        #endregion

        #region GoForward methods

        public bool CanGoForward => FrameFacade.CanGoForward;

        public void GoForward() => GoForwardAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        public async Task<bool> GoForwardAsync()
        {
            if (!FrameFacade.CanGoForward)
            {
                return false;
            }
            var next = FrameFacade.ForwardStack.FirstOrDefault();
            var parameter = SerializationService.Deserialize(next.Parameter?.ToString());
            return await NavigationOrchestratorAsync(next.SourcePageType, parameter, NavigationMode.Forward, () =>
            {
                try
                {
                FrameFacade.GoForward();
                return true;
                }
                catch (Exception ex)
                {
                    // Catch and ignore exceptions
                    DebugWrite(ex.Message, Severities.Error);
                    return false;
                }
            });
        }

        #endregion

        [Obsolete("Call FrameFacade.ClearCache(). This may be private in future versions.", false)]
        public void ClearCache(bool removeCachedPagesInBackStack = false) => FrameFacade.ClearCache(removeCachedPagesInBackStack);

        public void ClearHistory() => FrameFacade.BackStack.Clear();

        public void Resuming() { /* nothing */ }

        public async Task SuspendingAsync()
        {
            DebugWrite($"Frame: {FrameFacade.FrameId}");

            await SaveAsync();

            var page = FrameFacade.Content as Page;
            await Navigation.NavedFromAsync(page?.DataContext, page, true);
        }
    }
}

