
using AnimationImage.Core;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;

#if WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace AnimationImage.WPF
#endif

#if AVALONIA
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using FrameworkElement = Avalonia.Controls.Control;

namespace AnimationImage.Avalonia
#endif
{
	public abstract partial class AnimatableBitmap : INotifyPropertyChanged
	{
		protected Stream _stream;
		protected double CurrentTime { get; private set; }
		protected FrameworkElement Target;
		
		private bool _waitForResume = false;
		private Stopwatch _tpsWatcher;
		private int _tpsCount;

		private WriteableBitmap _frame;
		public WriteableBitmap Frame
		{
			get => _frame;
			protected set
			{
				if (_frame != value)
				{
					_frame = value;
					this.RasiePropertyChanged();
				}
			}
		}

		public AnimationState State { get; protected set; } = AnimationState.None;

		public Metadata Metadata { get; protected set; }

		private double _tps;
		/// <summary>
		/// 每秒更新次数（Ticks Per Second），表示动画实际更新的频率，数值越高动画越流畅。
		/// 启用TPS统计后可以通过绑定此属性来监控动画的性能表现。
		/// </summary>
		public double TPS
		{
			get => _tps;
			private set
			{
				if (_tps != value)
				{
					_tps = value;
					this.RasiePropertyChanged();
				}
			}
		}

		public virtual bool IsAnimatable => Frame != null
										&& Target != null
										&& Target.IsVisible
										&& State != AnimationState.Error;

		public event PropertyChangedEventHandler? PropertyChanged;
		protected void RasiePropertyChanged([CallerMemberName] string name = null)
		{
			this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}

		public ICommand BeginCommand { get; }
		public ICommand PauseCommand { get; }
		public ICommand StopCommand { get; }

		public AnimatableBitmap(AnimatableBitmapOptions options)
		{
			var source = options.Source;
			if (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps)
			{
				using var client = new HttpClient();
				using var rsp = client.GetAsync(source).Result;
				if (rsp?.IsSuccessStatusCode == true)
				{
					_stream = new MemoryStream();
					rsp.Content.CopyToAsync(_stream).Wait();
					_stream.Position = 0;
				}
			}
#if WPF
			else if (source.Scheme == "pack")
			{
				_stream = Application.GetResourceStream(source)?.Stream
					  ?? Application.GetContentStream(source)?.Stream
					  ?? Application.GetRemoteStream(source)?.Stream;
			}

#endif
#if AVALONIA
			else if (source.Scheme == "avares")
			{
				_stream = AssetLoader.Open(source);
			}
#endif
			else if (source.IsFile)
			{
				_stream = File.OpenRead(source.LocalPath);
			}

			if (_stream == null)
			{
				throw new IOException($"读取资源失败：{source}");
			}

			this.BeginCommand = new RelayCommand(this.BeginAnimation, () => this.IsAnimatable && State != AnimationState.Playing);
			this.PauseCommand = new RelayCommand(this.PauseAnimation, () => State == AnimationState.Playing);
			this.StopCommand = new RelayCommand(this.StopAnimation);

			if (EnableTPS)
			{
				_tpsWatcher = Stopwatch.StartNew();
				_tpsCount = 0;
			}
		}

		internal virtual void SeekTime(double milliseconds)
		{
			this.CurrentTime = milliseconds;
			if (EnableTPS)
			{
				_tpsCount++;
				if (_tpsWatcher.ElapsedMilliseconds >= 1000)
				{
					this.TPS = _tpsCount * 1000.0 / _tpsWatcher.ElapsedMilliseconds;
					_tpsWatcher.Restart();
					_tpsCount = 0;
				}
			}
		}

		protected bool _disposed;
		public void Dispose()
		{
			Dispose(true);
			GC.SuppressFinalize(this); 
		}

		#region static

		/// <summary>
		/// 是否启用TPS（每秒更新次数）统计，启用后可以通过绑定TPS属性来监控动画的实际更新频率
		/// </summary>
		/// <remarks>
		/// 默认在调试模式下启用，发布模式下禁用。
		/// </remarks>
		public static bool EnableTPS { get; set; }

		internal static SKImageInfo CreateDecodeInfo(int width, int height)
		{
			return new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
		}

		static AnimatableBitmap()
		{
#if DEBUG
			EnableTPS = true;
#endif
		}

		#endregion
	}
}
