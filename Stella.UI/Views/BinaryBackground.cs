using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Stella.UI.Views;

public class BinaryBackground : Control
{
    private readonly List<StreamColumn> _columns = new();
    private readonly Random _random = new();
    private readonly Typeface _typeface = new(FontFamily.Default);
    private DispatcherTimer? _timer;

    public static readonly StyledProperty<IBrush> CodeBrushProperty =
        AvaloniaProperty.Register<BinaryBackground, IBrush>(nameof(CodeBrush), new SolidColorBrush(Color.FromArgb(35, 255, 255, 255))); 

    public IBrush CodeBrush
    {
        get => GetValue(CodeBrushProperty);
        set => SetValue(CodeBrushProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (s, e) => InvalidateVisual());
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
    }

    private void InitializeColumns(double width, double height)
    {
        _columns.Clear();
        int fontSize = 10; 
        int columnCount = (int)(width / 12); 

        for (int i = 0; i < columnCount; i++)
        {
            _columns.Add(new StreamColumn
            {
                X = i * 12,
                Y = _random.NextDouble() * -height,
                Speed = _random.NextDouble() * 3 + 1.5,
                Characters = GenerateRandomBinaryString(20)
            });
        }
    }

    private string GenerateRandomBinaryString(int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
        {
            chars[i] = _random.Next(2) == 0 ? '0' : '1';
        }
        return new string(chars);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;

        if (width == 0 || height == 0) return;
        if (_columns.Count == 0) InitializeColumns(width, height);

        
        foreach (var col in _columns)
        {
            col.Y += col.Speed;
            if (col.Y > height)
            {
                col.Y = _random.NextDouble() * -100;
                col.Speed = _random.NextDouble() * 3 + 1.5;
                col.Characters = GenerateRandomBinaryString(20);
            }

            
            for (int i = 0; i < col.Characters.Length; i++)
            {
                double charY = col.Y + (i * 12);
                if (charY < 0 || charY > height) continue;

                var text = new FormattedText(
                    col.Characters[i].ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    10,
                    CodeBrush
                );

                context.DrawText(text, new Point(col.X, charY));
            }
        }
    }

    private class StreamColumn
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Speed { get; set; }
        public string Characters { get; set; } = "";
    }
}