using System;
using System.Drawing;
using RateTray.Configuration;
using RateTray.Ui;
using Xunit;

namespace RateTray.Tests;

public class ForkWidgetLayoutTests
{
    [Fact]
    public void Normalize_LimitaEscala()
    {
        var config = new WidgetOptions { Scale = 0.5 }.Normalize();
        Assert.Equal(0.6, config.Scale);

        config = new WidgetOptions { Scale = 2.0 }.Normalize();
        Assert.Equal(1.8, config.Scale);

        config = new WidgetOptions { Scale = 1.0 }.Normalize();
        Assert.Equal(1.0, config.Scale);
    }

    [Fact]
    public void Normalize_RejeitaOrientacaoInvalida()
    {
        var config = new WidgetOptions { Orientation = "diagonal" }.Normalize();
        Assert.Equal("vertical", config.Orientation);

        config = new WidgetOptions { Orientation = "HORIZONTAL" }.Normalize();
        Assert.Equal("horizontal", config.Orientation);
    }

    [Fact]
    public void CalculateLayout_TamanhoVertical()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, rows) = WidgetForm.CalculateLayout("vertical", 1.0, "topRight", 2, area, 96);
        
        // BaseWidth = 66, PadTop = 6, RowHeight = 74
        // Width = 66, Height = 6*2 + 2*74 = 12 + 148 = 160
        Assert.Equal(66, bounds.Width);
        Assert.Equal(160, bounds.Height);
        Assert.Equal(2, rows.Length);
        Assert.Equal(66, rows[0].Width);
        Assert.Equal(74, rows[0].Height);
    }

    [Fact]
    public void CalculateLayout_TamanhoHorizontal()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds, rows) = WidgetForm.CalculateLayout("horizontal", 1.0, "topLeft", 3, area, 96);
        
        // PadTop = 6, RowHeight = 74, BaseWidth = 66
        // Width = 6*2 + 3*74 = 12 + 222 = 234, Height = 66
        Assert.Equal(234, bounds.Width);
        Assert.Equal(66, bounds.Height);
        Assert.Equal(3, rows.Length);
        Assert.Equal(74, rows[0].Width);
        Assert.Equal(66, rows[0].Height);
    }

    [Fact]
    public void CalculateLayout_EscalaModificaDimensoes()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        var (bounds06, _) = WidgetForm.CalculateLayout("vertical", 0.6, "bottomRight", 1, area, 96);
        var (bounds18, _) = WidgetForm.CalculateLayout("vertical", 1.8, "bottomRight", 1, area, 96);
        
        Assert.True(bounds06.Width < bounds18.Width);
        Assert.True(bounds06.Height < bounds18.Height);
    }

    [Fact]
    public void CalculateLayout_PosicaoTopCenterEBottomCenter()
    {
        var area = new Rectangle(0, 0, 1920, 1080);
        
        var (boundsTopCenter, _) = WidgetForm.CalculateLayout("horizontal", 1.0, "topCenter", 2, area, 96);
        var expectedX = (1920 - boundsTopCenter.Width) / 2;
        // EdgeMargin = 12
        var expectedY = 12;
        Assert.Equal(expectedX, boundsTopCenter.X);
        Assert.Equal(expectedY, boundsTopCenter.Y);

        var (boundsBottomCenter, _) = WidgetForm.CalculateLayout("vertical", 1.0, "bottomCenter", 2, area, 96);
        var expectedXBottom = (1920 - boundsBottomCenter.Width) / 2;
        var expectedYBottom = 1080 - boundsBottomCenter.Height - 12;
        Assert.Equal(expectedXBottom, boundsBottomCenter.X);
        Assert.Equal(expectedYBottom, boundsBottomCenter.Y);
    }
}
