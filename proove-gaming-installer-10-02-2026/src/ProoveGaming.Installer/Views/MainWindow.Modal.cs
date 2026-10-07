using System.Windows.Input;
using Avalonia.Media;

namespace ProoveGaming.Installer.Views;

internal partial class MainWindow
{
	private bool _isCancelModalOpen;

	private ICommand OpenCancelModalCommand => new DelegateCommand(OpenCancelModal);
	private ICommand CloseCancelModalCommand => new DelegateCommand(CloseCancelModal);
	private ICommand ConfirmCancelModalCommand => new DelegateCommand(ConfirmCancelModal);

	private void OpenCancelModal()
	{
		if (_isCancelModalOpen)
		{
			return;
		}

		if (_installInProgress)
		{
			_installPauseGate?.Pause();
		}

		_isCancelModalOpen = true;

		GetCancelModalOverlay().IsVisible = true;
		GetCancelModalHost().IsVisible = true;
		// Blur the background layout, not just the content: the background image lives behind
		// MainContentGrid, so blurring only that left the backdrop sharp and the blur appeared to
		// cover just the middle of the window. The modal is a sibling of this subtree, so it stays
		// crisp.
		GetBackgroundLayoutRoot().Effect = new BlurEffect { Radius = 55 };
	}

	private void CloseCancelModal()
	{
		if (!_isCancelModalOpen)
		{
			return;
		}

		if (_installInProgress)
		{
			_installPauseGate?.Resume();
		}

		_isCancelModalOpen = false;
		GetCancelModalHost().IsVisible = false;
		GetCancelModalOverlay().IsVisible = false;
		GetBackgroundLayoutRoot().Effect = null;
	}

	private void ConfirmCancelModal()
	{
		if (_installInProgress)
		{
			_closeAfterInstallCancellation = true;
			_installCancellationTokenSource?.Cancel();
			CloseCancelModal();
			return;
		}

		Close();
	}

	private sealed class DelegateCommand(Action execute) : ICommand
	{
		// Required by ICommand but never raised: CanExecute is unconditionally true, so there is
		// nothing for a subscriber to be notified about.
		public event EventHandler? CanExecuteChanged { add { } remove { } }

		public bool CanExecute(object? parameter) => true;

		public void Execute(object? parameter) => execute();
	}
}
