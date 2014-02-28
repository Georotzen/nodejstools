﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.NodejsTools.Npm;

namespace Microsoft.NodejsTools.NpmUI {
    class NpmOutputControlViewModel : INotifyPropertyChanged, IDisposable {
        private INpmController _npmController;
        private readonly Queue<QueuedNpmCommandInfo> _commandQueue = new Queue<QueuedNpmCommandInfo>();
        private readonly object _lock = new object();
        private bool _isDisposed;
        private string _statusText = Resources.NpmStatusReady;
        private bool _isExecutingCommand;
        private bool _withErrors;
        private readonly FlowDocument _output = new FlowDocument();
        private readonly Thread _worker;
        private QueuedNpmCommandInfo _currentCommand;
        private INpmCommander _commander;
        
        public NpmOutputControlViewModel() {
            var style = new Style(typeof(Paragraph));
            style.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            _output.Resources.Add(typeof(Paragraph), style);

            _worker = new Thread(Run);
            _worker.Name = "npm UI Execution";
            _worker.IsBackground = true;
            _worker.Start();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null) {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
        }

        private void Pulse() {
            lock (_lock) {
                Monitor.PulseAll(_lock);
            }
        }

        public INpmController NpmController {
            get { return _npmController; }
            set {
                _npmController = value;   
                OnPropertyChanged();
                Pulse();
            }
        }

        public string StatusText {
            get { return _statusText; }
            set {
                _statusText = value;
                OnPropertyChanged();
            }
        }

        public bool IsExecutingCommand {
            get {
                lock (_lock) {
                    return _isExecutingCommand;
                }
            }
            set {
                lock (_lock) {
                    _isExecutingCommand = value;
                    Pulse();
                }
                OnPropertyChanged();
                OnPropertyChanged("ExecutionProgressVisibility");
                OnPropertyChanged("IsCancellable");
            }
        }

        public Visibility ExecutionProgressVisibility {
            get { return IsExecutingCommand ? Visibility.Visible : Visibility.Hidden; }
        }

        public bool IsCancellable {
            get {
                lock (_lock) {
                    return _commandQueue.Count > 0 || IsExecutingCommand;
                }
            }
        }

        public void Cancel() {
            lock (_lock) {
                _commandQueue.Clear();
                if (null != _commander) {
                    _commander.CancelCurrentCommand();
                }
                IsExecutingCommand = false;
            }

            UpdateStatusMessage();
            OnPropertyChanged("IsCancellable");
        }

        private void QueueCommand(QueuedNpmCommandInfo info) {
            lock (_lock) {
                _commandQueue.Enqueue(info);
                Monitor.PulseAll(_lock);
            }

            UpdateStatusMessageSafe();
            OnPropertyChanged("IsCancellable");
        }

        public void QueueCommand(string arguments) {
            QueueCommand(new QueuedNpmCommandInfo(arguments));
        }

        public void QueueCommand(string command, string arguments) {
            QueueCommand(string.Format("{0} {1}", command, arguments));
        }

        public void QueueInstallPackage(
            string name,
            string version,
            DependencyType type) {
            QueueCommand(new QueuedNpmCommandInfo(name, version, type));
        }

        public void QueueInstallGlobalPackage(
            string name,
            string version) {
            QueueCommand(new QueuedNpmCommandInfo(name, version));
        }

        private async void Execute(QueuedNpmCommandInfo info) {
            IsExecutingCommand = true;
            try {
                lock (_lock) {
                    _commander = _npmController.CreateNpmCommander();
                    _commander.OutputLogged += commander_OutputLogged;
                    _commander.ErrorLogged += commander_ErrorLogged;
                    _commander.ExceptionLogged += commander_ExceptionLogged;
                    _commander.CommandCompleted += commander_CommandCompleted;
                }

                if (info.IsFreeformArgumentCommand) {
                    await _commander.ExecuteNpmCommandAsync(info.Arguments);
                } else if (info.IsGlobalInstall) {
                    await _commander.InstallGlobalPackageByVersionAsync(
                            info.Name,
                            info.Version);
                } else {
                    await _commander.InstallPackageByVersionAsync(
                                info.Name,
                                info.Version,
                                info.DependencyType,
                                true);
                }
            } finally {
                var cmdr = _commander;
                if (null != cmdr) {
                    cmdr.OutputLogged -= commander_OutputLogged;
                    cmdr.ErrorLogged -= commander_ErrorLogged;
                    cmdr.ExceptionLogged -= commander_ExceptionLogged;
                    cmdr.CommandCompleted -= commander_CommandCompleted;
                    lock (_lock) {
                        _commander = null;
                    }
                }
            }
        }

        private void HandleCompletionSafe() {
            UpdateStatusMessage();
            OnPropertyChanged("IsCancellable");
        }

        private void commander_CommandCompleted(object sender, NpmCommandCompletedEventArgs e) {
            IsExecutingCommand = false;
            Application.Current.Dispatcher.BeginInvoke(
                new Action(HandleCompletionSafe));
        }

        public FlowDocument Output {
            get { return _output; }
        }

        public event EventHandler OutputWritten;

        private void OnOutputWritten() {
            var handlers = OutputWritten;
            if (null != handlers) {
                handlers(this, EventArgs.Empty);
            }
        }

        private string Preprocess(string source) {
            return source.EndsWith(Environment.NewLine) ? source.Substring(0, source.Length - Environment.NewLine.Length) : source;
        }

        private void WriteLines(string text, bool forceError) {
            text = Preprocess(text);
            if (forceError) {
                _withErrors = true;
            }
            foreach (var line in text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None)) {
                var sub = line;
                var paragraph = new Paragraph();

                if (sub.StartsWith("npm ")) {
                    paragraph.Inlines.Add(new Run(sub.Substring(0,4)));
                    sub = sub.Length > 4 ? sub.Substring(4) : string.Empty;
                    if (sub.StartsWith("ERR!")) {
                        _withErrors = true;
                        paragraph.Inlines.Add(new Run(sub.Substring(0, 4)) { Foreground = Brushes.Red });
                        sub = sub.Length > 4 ? sub.Substring(4) : string.Empty;
                    } else if (sub.StartsWith("WARN")) {
                        paragraph.Inlines.Add(new Run(sub.Substring(0, 4)) { Foreground = Brushes.Yellow });
                        sub = sub.Length > 4 ? sub.Substring(4) : string.Empty;
                    }
                }

                paragraph.Inlines.Add(new Run(sub));

                _output.Blocks.Add(paragraph);
            }

            OnOutputWritten();
        }

        private void commander_ExceptionLogged(object sender, NpmExceptionEventArgs e) {
            Application.Current.Dispatcher.BeginInvoke(
                new Action(() => WriteLines(ErrorHelper.GetExceptionDetailsText(e.Exception), true)));
        }

        private void commander_ErrorLogged(object sender, NpmLogEventArgs e) {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => WriteLines(e.LogText, false)));
        }

        private void commander_OutputLogged(object sender, NpmLogEventArgs e) {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => WriteLines(e.LogText, false)));
        }

        private string GetCommandText(QueuedNpmCommandInfo info) {
            var buff = new StringBuilder("npm ");
            if (info.IsFreeformArgumentCommand) {
                buff.Append(info.Arguments);
            } else {
                buff.Append(NpmArgumentBuilder.GetNpmInstallArguments(
                    info.Name,
                    info.Version,
                    info.DependencyType,
                    info.IsGlobalInstall,
                    true));
            }
            return buff.ToString();
        }

        private void UpdateStatusMessage() {
            bool                    executingCommand;
            QueuedNpmCommandInfo    command;
            int count;
            lock (_lock) {
                executingCommand = IsExecutingCommand;
                command = _currentCommand;
                count = _commandQueue.Count;
            }

            string status;

            if (executingCommand && null != command) {
                var commandText = GetCommandText(command);
                if (count > 0) {
                    status = string.Format(
                        _withErrors ? Resources.NpmStatusExecutingQueuedErrors : Resources.NpmStatusExecutingQueued,
                        commandText,
                        count);
                } else {
                    status = string.Format(
                        _withErrors ? Resources.NpmStatusExecutingErrors : Resources.NpmStatusExecuting,
                        commandText);
                }
            } else {
                status = _withErrors ? Resources.NpmStatusReadyWithErrors : Resources.NpmStatusReady;
            }

            StatusText = status;
        }

        private void UpdateStatusMessageSafe() {
            Application.Current.Dispatcher.BeginInvoke(new Action(UpdateStatusMessage));
        }

        private void Run() {
            int count = 0;
            // We want the thread to continue running queued commands before
            // exiting so the user can close the install window without having to wait
            // for commands to complete.
            while (!_isDisposed || count > 0) {
                lock (_lock) {
                    while ((_commandQueue.Count == 0 && !_isDisposed)
                        || null == _npmController
                        || IsExecutingCommand) {
                        Monitor.Wait(_lock);
                    }

                    if (_commandQueue.Count > 0) {
                        _currentCommand = _commandQueue.Dequeue();
                    }
                    count = _commandQueue.Count;
                }

                if (null != _currentCommand) {
                    Execute(_currentCommand);
                    UpdateStatusMessageSafe();
                }
            }
        }

        public void Dispose() {
            _isDisposed = true;
            OutputWritten = null;
            Pulse();
        }

        private class QueuedNpmCommandInfo : EventArgs {

            public QueuedNpmCommandInfo(
                string arguments) {
                Name = arguments;
                IsFreeformArgumentCommand = true;
            }

            public QueuedNpmCommandInfo(
                string name,
                string version) {
                Name = name;
                Version = version;
                IsGlobalInstall = true;
                IsFreeformArgumentCommand = false;
            }

            public QueuedNpmCommandInfo(
                string name,
                string version,
                DependencyType depType)
                : this(name, version) {
                DependencyType = depType;
                IsGlobalInstall = false;
            }

            public bool IsFreeformArgumentCommand { get; private set; }
            public string Arguments {
                get { return Name; }
            }
            public string Name { get; private set; }
            public string Version { get; private set; }
            public DependencyType DependencyType { get; private set; }
            public bool IsGlobalInstall { get; private set; }
        }
    }
}
