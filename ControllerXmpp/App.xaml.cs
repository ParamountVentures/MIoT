﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Waher.Content;
using Waher.Content.Xml;
using Waher.Events;
using Waher.Networking.XMPP;
using Waher.Networking.XMPP.BitsOfBinary;
using Waher.Networking.XMPP.Chat;
using Waher.Networking.XMPP.Control;
using Waher.Networking.XMPP.Sensor;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Persistence.Filters;
using Waher.Runtime.Inventory;
using Waher.Runtime.Settings;
using Waher.Things;
using Waher.Things.SensorData;

namespace ControllerXmpp
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
		private static App instance = null;
		private Timer secondTimer = null;
		private XmppClient xmppClient = null;
		private BobClient bobClient = null;
		private ChatServer chatServer = null;
		private SensorClient sensorClient = null;
		private ControlClient controlClient = null;
		private SensorServer sensorServer = null;
		private string deviceId;

		/// <summary>
		/// Initializes the singleton application object.  This is the first line of authored code
		/// executed, and as such is the logical equivalent of main() or WinMain().
		/// </summary>
		public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application
                }

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                // Ensure the current window is active
                Window.Current.Activate();
				Task.Run((Action)this.Init);
			}
		}

		private async void Init()
		{
			try
			{
				Log.Informational("Starting application.");

				Types.Initialize(
					typeof(FilesProvider).GetTypeInfo().Assembly,
					typeof(RuntimeSettings).GetTypeInfo().Assembly,
					typeof(IContentEncoder).GetTypeInfo().Assembly,
					typeof(XmppClient).GetTypeInfo().Assembly,
					typeof(Waher.Content.Markdown.MarkdownDocument).GetTypeInfo().Assembly,
					typeof(XML).GetTypeInfo().Assembly,
					typeof(Waher.Script.Expression).GetTypeInfo().Assembly,
					typeof(Waher.Script.Graphs.Graph).GetTypeInfo().Assembly,
					typeof(App).GetTypeInfo().Assembly);

				Database.Register(new FilesProvider(Windows.Storage.ApplicationData.Current.LocalFolder.Path +
					Path.DirectorySeparatorChar + "Data", "Default", 8192, 1000, 8192, Encoding.UTF8, 10000));

				this.deviceId = await RuntimeSettings.GetAsync("DeviceId", string.Empty);
				if (string.IsNullOrEmpty(this.deviceId))
				{
					this.deviceId = Guid.NewGuid().ToString().Replace("-", string.Empty);
					await RuntimeSettings.SetAsync("DeviceId", this.deviceId);
				}

				Log.Informational("Device ID: " + this.deviceId);

				string Host = await RuntimeSettings.GetAsync("XmppHost", "waher.se");
				int Port = (int)await RuntimeSettings.GetAsync("XmppPort", 5222);
				string UserName = await RuntimeSettings.GetAsync("XmppUserName", string.Empty);
				string PasswordHash = await RuntimeSettings.GetAsync("XmppPasswordHash", string.Empty);
				string PasswordHashMethod = await RuntimeSettings.GetAsync("XmppPasswordHashMethod", string.Empty);

				if (string.IsNullOrEmpty(Host) ||
					Port <= 0 || Port > ushort.MaxValue ||
					string.IsNullOrEmpty(UserName) ||
					string.IsNullOrEmpty(PasswordHash) ||
					string.IsNullOrEmpty(PasswordHashMethod))
				{
					await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
						async () => await this.ShowConnectionDialog(Host, Port, UserName));
				}
				else
				{
					this.xmppClient = new XmppClient(Host, Port, UserName, PasswordHash, PasswordHashMethod, "en",
						typeof(App).GetTypeInfo().Assembly)     // Add "new LogSniffer()" to the end, to output communication to the log.
					{
						AllowCramMD5 = false,
						AllowDigestMD5 = false,
						AllowPlain = false,
						AllowScramSHA1 = true
					};
					this.xmppClient.OnStateChanged += this.StateChanged;
					this.xmppClient.OnConnectionError += this.ConnectionError;
					this.AttachFeatures();

					Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
					this.xmppClient.Connect();
				}
			}
			catch (Exception ex)
			{
				Log.Emergency(ex);

				MessageDialog Dialog = new MessageDialog(ex.Message, "Error");
				await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
					async () => await Dialog.ShowAsync());
			}
		}

		private async Task ShowConnectionDialog(string Host, int Port, string UserName)
		{
			try
			{
				AccountDialog Dialog = new AccountDialog(Host, Port, UserName);

				switch (await Dialog.ShowAsync())
				{
					case ContentDialogResult.Primary:
						if (this.xmppClient != null)
						{
							this.xmppClient.Dispose();
							this.xmppClient = null;
						}

						this.xmppClient = new XmppClient(Dialog.Host, Dialog.Port, Dialog.UserName, Dialog.Password, "en", typeof(App).GetTypeInfo().Assembly)
						{
							AllowCramMD5 = false,
							AllowDigestMD5 = false,
							AllowPlain = false
						};

						this.xmppClient.AllowRegistration();                // Allows registration on servers that do not require signatures.
																			// this.xmppClient.AllowRegistration(Key, Secret);	// Allows registration on servers requiring a signature of the registration request.

						this.xmppClient.OnStateChanged += this.TestConnectionStateChanged;
						this.xmppClient.OnConnectionError += this.ConnectionError;

						Log.Informational("Connecting to " + this.xmppClient.Host + ":" + this.xmppClient.Port.ToString());
						this.xmppClient.Connect();
						break;

					case ContentDialogResult.Secondary:
						break;
				}
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

		private void StateChanged(object Sender, XmppState State)
		{
			Log.Informational("Changing state: " + State.ToString());

			if (State == XmppState.Connected)
			{
				Log.Informational("Connected as " + this.xmppClient.FullJID);
				Task T = this.SetVCard();
			}
		}

		private void ConnectionError(object Sender, Exception ex)
		{
			Log.Error(ex.Message);
		}

		private void AttachFeatures()
		{
			this.sensorServer = new SensorServer(this.xmppClient, true);
			this.sensorServer.OnExecuteReadoutRequest += (sender, e) =>
			{
			};

			this.xmppClient.OnError += (Sender, ex) => Log.Error(ex);
			this.xmppClient.OnPasswordChanged += (Sender, e) => Log.Informational("Password changed.", this.xmppClient.BareJID);

			this.xmppClient.OnPresenceSubscribe += (Sender, e) =>
			{
				Log.Informational("Accepting friendship request.", this.xmppClient.BareJID, e.From);
				e.Accept();
			};

			this.xmppClient.OnPresenceUnsubscribe += (Sender, e) =>
			{
				Log.Informational("Friendship removed.", this.xmppClient.BareJID, e.From);
				e.Accept();
			};

			this.xmppClient.OnPresenceSubscribed += (Sender, e) => Log.Informational("Friendship request accepted.", this.xmppClient.BareJID, e.From);
			this.xmppClient.OnPresenceUnsubscribed += (Sender, e) => Log.Informational("Friendship removal accepted.", this.xmppClient.BareJID, e.From);

			this.bobClient = new BobClient(this.xmppClient, Path.Combine(Path.GetTempPath(), "BitsOfBinary"));
			this.chatServer = new ChatServer(this.xmppClient, this.bobClient, this.sensorServer);

			// XEP-0054: vcard-temp: http://xmpp.org/extensions/xep-0054.html
			this.xmppClient.RegisterIqGetHandler("vCard", "vcard-temp", this.QueryVCardHandler, true);
		}

		private async void TestConnectionStateChanged(object Sender, XmppState State)
		{
			try
			{
				this.StateChanged(Sender, State);

				switch (State)
				{
					case XmppState.Connected:
						await RuntimeSettings.SetAsync("XmppHost", this.xmppClient.Host);
						await RuntimeSettings.SetAsync("XmppPort", this.xmppClient.Port);
						await RuntimeSettings.SetAsync("XmppUserName", this.xmppClient.UserName);
						await RuntimeSettings.SetAsync("XmppPasswordHash", this.xmppClient.PasswordHash);
						await RuntimeSettings.SetAsync("XmppPasswordHashMethod", this.xmppClient.PasswordHashMethod);

						this.xmppClient.OnStateChanged -= this.TestConnectionStateChanged;
						this.xmppClient.OnStateChanged += this.StateChanged;
						this.AttachFeatures();
						await this.SetVCard();
						break;

					case XmppState.Error:
					case XmppState.Offline:
						await MainPage.Instance.Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
							async () => await this.ShowConnectionDialog(this.xmppClient.Host, this.xmppClient.Port, this.xmppClient.UserName));
						break;
				}
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

		private async void QueryVCardHandler(object Sender, IqEventArgs e)
		{
			try
			{
				e.IqResult(await this.GetVCardXml());
			}
			catch (Exception ex)
			{
				e.IqError(ex);
			}
		}

		private async Task SetVCard()
		{
			Log.Informational("Setting vCard");

			// XEP-0054 - vcard-temp: http://xmpp.org/extensions/xep-0054.html

			this.xmppClient.SendIqSet(string.Empty, await this.GetVCardXml(), (sender, e) =>
			{
				if (e.Ok)
					Log.Informational("vCard successfully set.");
				else
					Log.Error("Unable to set vCard.");
			}, null);
		}

		private async Task<string> GetVCardXml()
		{
			StringBuilder Xml = new StringBuilder();

			Xml.Append("<vCard xmlns='vcard-temp'>");
			Xml.Append("<FN>MIoT Controller</FN><N><FAMILY>Controller</FAMILY><GIVEN>MIoT</GIVEN><MIDDLE/></N>");
			Xml.Append("<URL>https://github.com/PeterWaher/MIoT</URL>");
			Xml.Append("<JABBERID>");
			Xml.Append(XML.Encode(this.xmppClient.BareJID));
			Xml.Append("</JABBERID>");
			Xml.Append("<UID>");
			Xml.Append(this.deviceId);
			Xml.Append("</UID>");
			Xml.Append("<DESC>XMPP Controller Project (ControllerXmpp) from the book Mastering Internet of Things, by Peter Waher.</DESC>");

			// XEP-0153 - vCard-Based Avatars: http://xmpp.org/extensions/xep-0153.html

			StorageFile File = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/LargeTile.scale-100.png"));
			byte[] Icon = System.IO.File.ReadAllBytes(File.Path);

			Xml.Append("<PHOTO><TYPE>image/png</TYPE><BINVAL>");
			Xml.Append(Convert.ToBase64String(Icon));
			Xml.Append("</BINVAL></PHOTO>");
			Xml.Append("</vCard>");

			return Xml.ToString();
		}

		/// <summary>
		/// Invoked when Navigation to a certain page fails
		/// </summary>
		/// <param name="sender">The Frame which failed navigation</param>
		/// <param name="e">Details about the navigation failure</param>
		void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();

			instance = null;

			if (this.chatServer != null)
			{
				this.chatServer.Dispose();
				this.chatServer = null;
			}

			if (this.bobClient != null)
			{
				this.bobClient.Dispose();
				this.bobClient = null;
			}

			if (this.sensorServer != null)
			{
				this.sensorServer.Dispose();
				this.sensorServer = null;
			}

			if (this.sensorClient != null)
			{
				this.sensorClient.Dispose();
				this.sensorClient = null;
			}

			if (this.controlClient != null)
			{
				this.controlClient.Dispose();
				this.controlClient = null;
			}

			if (this.xmppClient != null)
			{
				this.xmppClient.Dispose();
				this.xmppClient = null;
			}

			if (this.secondTimer != null)
			{
				this.secondTimer.Dispose();
				this.secondTimer = null;
			}

			Log.Terminate();

			deferral.Complete();
        }
    }
}