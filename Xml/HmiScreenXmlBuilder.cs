using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using TiaMcpServer;

namespace TiaMcpServer.Xml;

/// <summary>
/// 支持控件：Rectangle、Circle、TextField、Button、IOField、Switch、Line、Group、SymbolicIOField、GraphicView
/// 支持动画：RangeAppearanceAnimation、VisibilityAnimation、SingleBitVisibilityAnimation、ObjectEnablingAnimation
/// 支持事件：Click/Press/Release/SwitchOn/SwitchOff → InvertBit/SetBit/ResetBit/ActivateScreen/SetTag
/// 支持绑定：TagConnectionDynamic、TextList、Picture；支持多画面层。
/// </summary>
public class HmiScreenXmlBuilder
{
    private int _nextId;
    private int _buttonTabIndex;
    private readonly XmlDocument _doc;

    public HmiScreenXmlBuilder(int startId = 0x100)
    {
        _nextId = startId;
        _doc = new XmlDocument();
    }

    public string NextId() => (_nextId++).ToString("X");

    private XmlElement CreateElement(string name) => _doc.CreateElement(name);

    private static void AddAttribute(XmlElement parent, string name, string value)
    {
        var doc = parent.OwnerDocument ?? throw new InvalidOperationException("parent has no OwnerDocument");
        var elem = doc.CreateElement(name);
        // ★V17 校准：颜色属性必须为 "R, G, B"（逗号后带空格）格式，无空格形式（如 "70,130,220"）
        // 会被 V17 导入器拒绝。三数字模式自动规范化。
        elem.InnerText = NormalizeColorValue(value);
        parent.AppendChild(elem);
    }

    /// <summary>把 "R,G,B" 无空格颜色值规范化为 "R, G, B"（非颜色值原样返回）。</summary>
    private static string NormalizeColorValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var trimmed = value.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d{1,3},\d{1,3},\d{1,3}$"))
            return System.Text.RegularExpressions.Regex.Replace(trimmed, @"(\d+),(\d+),(\d+)", "$1, $2, $3");
        return value;
    }

    /// <summary>
    /// 创建多语言文本元素（使用this._doc作为工厂）
    /// richText=false 时输出纯文本（用于 Switch 的 CaptionText，TIA 按纯文本解析）
    /// </summary>
    public XmlElement CreateMultilingualText(string compositionName, string? text = null, string culture = "zh-CN", bool richText = true)
    {
        var ml = CreateElement("MultilingualText");
        ml.SetAttribute("ID", NextId());
        ml.SetAttribute("CompositionName", compositionName);

        var objList = CreateElement("ObjectList");
        var item = CreateElement("MultilingualTextItem");
        item.SetAttribute("ID", NextId());
        item.SetAttribute("CompositionName", "Items");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "Culture", culture);
        var textElem = CreateElement("Text");
        if (!string.IsNullOrEmpty(text))
        {
            if (richText)
                textElem.InnerXml = $"<body><p>{System.Security.SecurityElement.Escape(text)}</p></body>";
            else
                textElem.InnerText = text;
        }
        attr.AppendChild(textElem);
        item.AppendChild(attr);

        objList.AppendChild(item);
        ml.AppendChild(objList);
        return ml;
    }

    /// <summary>
    /// 创建字体元素（使用this._doc作为工厂）
    /// </summary>
    public XmlElement CreateFont(string family = "宋体", string size = "12", string style = "Regular", string compositionName = "Font")
    {
        var font = CreateElement("Hmi.Globalization.MultiLingualFont");
        font.SetAttribute("ID", NextId());
        font.SetAttribute("CompositionName", compositionName);

        var objList = CreateElement("ObjectList");
        var item = CreateElement("Hmi.Globalization.FontItem");
        item.SetAttribute("ID", NextId());
        item.SetAttribute("CompositionName", "Items");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "Culture", "zh-CN");
        AddAttribute(attr, "FontFamily", family);
        AddAttribute(attr, "FontSize", size);
        AddAttribute(attr, "FontStyle", style);
        item.AppendChild(attr);

        objList.AppendChild(item);
        font.AppendChild(objList);
        return font;
    }

    /// <summary>
    /// 创建Tag链接元素（用于LinkList）
    /// </summary>
    public XmlElement CreateTagLink(string tagName)
    {
        var tag = CreateElement("Tag");
        tag.SetAttribute("TargetID", "@OpenLink");
        AddAttribute(tag, "Name", tagName);
        return tag;
    }

    /// <summary>
    /// 创建Value链接元素（用于事件参数）
    /// </summary>
    public XmlElement CreateValueLink(string valueName)
    {
        var val = CreateElement("Value");
        val.SetAttribute("TargetID", "@OpenLink");
        AddAttribute(val, "Name", valueName);
        return val;
    }

    public XmlElement CreateTextListLink(string textListName)
    {
        var link = CreateElement("TextList");
        link.SetAttribute("TargetID", "@OpenLink");
        AddAttribute(link, "Name", textListName);
        return link;
    }

    public XmlElement CreatePictureLink(string pictureName)
    {
        var link = CreateElement("Picture");
        link.SetAttribute("TargetID", "@OpenLink");
        AddAttribute(link, "Name", pictureName);
        return link;
    }

    private XmlElement CreateTagProperty(string propertyName, string tagName)
    {
        var prop = CreateElement("Hmi.Screen.Property");
        prop.SetAttribute("ID", NextId());
        prop.SetAttribute("CompositionName", "Properties");
        var propAttr = CreateElement("AttributeList");
        AddAttribute(propAttr, "Name", propertyName);
        prop.AppendChild(propAttr);

        var propObj = CreateElement("ObjectList");
        var dyn = CreateElement("Hmi.Dynamic.TagConnectionDynamic");
        dyn.SetAttribute("ID", NextId());
        dyn.SetAttribute("CompositionName", "Dynamic");
        var dynAttr = CreateElement("AttributeList");
        AddAttribute(dynAttr, "Indirect", "false");
        dyn.AppendChild(dynAttr);
        var dynLink = CreateElement("LinkList");
        dynLink.AppendChild(CreateTagLink(tagName));
        dyn.AppendChild(dynLink);
        propObj.AppendChild(dyn);
        prop.AppendChild(propObj);
        return prop;
    }

    public void ApplyAnimations(XmlElement screenItem, IEnumerable<HmiAnimationSpec>? animations)
    {
        if (animations == null) return;
        var list = animations.Where(a => a != null).ToList();
        if (list.Count == 0) return;
        var objectList = screenItem["ObjectList"];
        if (objectList == null)
        {
            objectList = CreateElement("ObjectList");
            screenItem.AppendChild(objectList);
        }
        foreach (var animation in list)
        {
            if (string.IsNullOrWhiteSpace(animation.Tag))
                throw new InvalidOperationException("HMI 动画必须提供 Tag。");
            var type = (animation.Type ?? "").Trim().ToLowerInvariant();
            objectList.AppendChild(type switch
            {
                "visibility" or "rangevisibility" => CreateVisibilityAnimation(animation.Tag, animation.RangeStart, animation.RangeEnd, animation.Visible),
                "singlebitvisibility" or "bitvisibility" => CreateSingleBitVisibilityAnimation(animation.Tag, animation.BitPosition, animation.Visible),
                "enabling" or "objectenabling" => CreateObjectEnablingAnimation(animation.Tag, animation.RangeStart, animation.RangeEnd, animation.ObjectEnabled),
                _ => throw new InvalidOperationException($"不支持的 HMI 动画类型 '{animation.Type}'。")
            });
        }
    }

    private XmlElement CreateVisibilityAnimation(string tagName, int rangeStart, int rangeEnd, bool visible)
    {
        var anim = CreateElement("Hmi.Dynamic.VisibilityAnimation");
        anim.SetAttribute("ID", NextId());
        anim.SetAttribute("CompositionName", "Animations");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "Name", "VisibilityAnimation");
        AddAttribute(attr, "RangeEnd", rangeEnd.ToString());
        AddAttribute(attr, "RangeStart", rangeStart.ToString());
        AddAttribute(attr, "Visible", visible ? "true" : "false");
        anim.AppendChild(attr);
        var obj = CreateElement("ObjectList");
        var trigger = CreateElement("Hmi.Dynamic.TagElementTrigger");
        trigger.SetAttribute("ID", NextId());
        trigger.SetAttribute("CompositionName", "VisibilityTag");
        var links = CreateElement("LinkList");
        links.AppendChild(CreateTagLink(tagName));
        trigger.AppendChild(links);
        obj.AppendChild(trigger);
        anim.AppendChild(obj);
        return anim;
    }

    private XmlElement CreateSingleBitVisibilityAnimation(string tagName, int bitPosition, bool visible)
    {
        if (bitPosition < 0 || bitPosition > 31)
            throw new InvalidOperationException("SingleBitVisibilityAnimation 的 BitPosition 必须在 0..31。 ");
        var anim = CreateElement("Hmi.Dynamic.SingleBitVisibilityAnimation");
        anim.SetAttribute("ID", NextId());
        anim.SetAttribute("CompositionName", "Animations");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "BitPosition", bitPosition.ToString());
        AddAttribute(attr, "Name", "VisibilityAnimation");
        AddAttribute(attr, "Visible", visible ? "true" : "false");
        anim.AppendChild(attr);
        var obj = CreateElement("ObjectList");
        var trigger = CreateElement("Hmi.Dynamic.TagElementTrigger");
        trigger.SetAttribute("ID", NextId());
        trigger.SetAttribute("CompositionName", "VisibilityTag");
        var links = CreateElement("LinkList");
        links.AppendChild(CreateTagLink(tagName));
        trigger.AppendChild(links);
        obj.AppendChild(trigger);
        anim.AppendChild(obj);
        return anim;
    }

    private XmlElement CreateObjectEnablingAnimation(string tagName, int rangeStart, int rangeEnd, bool objectEnabled)
    {
        var anim = CreateElement("Hmi.Dynamic.ObjectEnablingAnimation");
        anim.SetAttribute("ID", NextId());
        anim.SetAttribute("CompositionName", "Animations");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "Name", "ObjectEnablingAnimation");
        AddAttribute(attr, "ObjectEnabled", objectEnabled ? "true" : "false");
        AddAttribute(attr, "RangeEnd", rangeEnd.ToString());
        AddAttribute(attr, "RangeStart", rangeStart.ToString());
        anim.AppendChild(attr);
        var obj = CreateElement("ObjectList");
        var trigger = CreateElement("Hmi.Dynamic.TagElementTrigger");
        trigger.SetAttribute("ID", NextId());
        trigger.SetAttribute("CompositionName", "ObjectEnablingTag");
        var links = CreateElement("LinkList");
        links.AppendChild(CreateTagLink(tagName));
        trigger.AppendChild(links);
        obj.AppendChild(trigger);
        anim.AppendChild(obj);
        return anim;
    }

    /// <summary>
    /// 创建矩形控件（面板背景）
    /// </summary>
    public XmlElement CreateRectangle(
        string name, int left, int top, int width, int height,
        string fillColor = "220,220,220", string borderColor = "100,100,100",
        int borderWidth = 2, int cornerRadius = 8)
    {
        var rect = CreateElement("Hmi.Screen.Rectangle");
        rect.SetAttribute("ID", NextId());
        rect.SetAttribute("CompositionName", "ScreenItems");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "BackColor", fillColor);
        AddAttribute(attr, "BackFillStyle", "Solid");
        AddAttribute(attr, "BorderColor", borderColor);
        AddAttribute(attr, "BorderWidth", borderWidth.ToString());
        AddAttribute(attr, "EdgeStyle", "Solid");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "RoundCornerHeight", cornerRadius.ToString());
        AddAttribute(attr, "RoundCornerWidth", cornerRadius.ToString());
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "UseDesignColorSchema", "false");
        AddAttribute(attr, "Width", width.ToString());
        rect.AppendChild(attr);

        return rect;
    }

    /// <summary>
    /// 创建圆形指示灯控件（带颜色动画）
    /// </summary>
    public XmlElement CreateCircleIndicator(
        string name, int left, int top, int radius, string tagName,
        string offColor = "255,0,0", string onColor = "0,255,0")
    {
        var circle = CreateElement("Hmi.Screen.Circle");
        circle.SetAttribute("ID", NextId());
        circle.SetAttribute("CompositionName", "ScreenItems");

        var size = (radius * 2).ToString();
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "BackColor", offColor);
        AddAttribute(attr, "BackFillStyle", "Solid");
        AddAttribute(attr, "BorderColor", "24,28,49");
        AddAttribute(attr, "BorderWidth", "0");
        AddAttribute(attr, "EdgeStyle", "Solid");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "Height", size);
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "Radius", radius.ToString());
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "UseDesignColorSchema", "false");
        // Keep the base indicator visible in the V17 editor; the range animation
        // still changes its color at runtime.
        AddAttribute(attr, "Width", size);
        circle.AppendChild(attr);

        var objList = CreateElement("ObjectList");

        var anim = CreateElement("Hmi.Dynamic.RangeAppearanceAnimation");
        anim.SetAttribute("ID", NextId());
        anim.SetAttribute("CompositionName", "Animations");
        var animAttr = CreateElement("AttributeList");
        AddAttribute(animAttr, "Name", "RangeAppearanceAnimation");
        anim.AppendChild(animAttr);

        var animObj = CreateElement("ObjectList");

        var trigger = CreateElement("Hmi.Dynamic.TagElementTrigger");
        trigger.SetAttribute("ID", NextId());
        trigger.SetAttribute("CompositionName", "RangeTag");
        var triggerLink = CreateElement("LinkList");
        triggerLink.AppendChild(CreateTagLink(tagName));
        trigger.AppendChild(triggerLink);
        animObj.AppendChild(trigger);

        var rangeOff = CreateElement("Hmi.Dynamic.Range");
        rangeOff.SetAttribute("ID", NextId());
        rangeOff.SetAttribute("CompositionName", "RangeValues");
        var rangeOffAttr = CreateElement("AttributeList");
        AddAttribute(rangeOffAttr, "BackColor", offColor);
        AddAttribute(rangeOffAttr, "FlashingType", "No");
        AddAttribute(rangeOffAttr, "ForeColor", "0,0,0");
        AddAttribute(rangeOffAttr, "LowerLimit", "0");
        AddAttribute(rangeOffAttr, "UpperLimit", "0");
        rangeOff.AppendChild(rangeOffAttr);
        animObj.AppendChild(rangeOff);

        var rangeOn = CreateElement("Hmi.Dynamic.Range");
        rangeOn.SetAttribute("ID", NextId());
        rangeOn.SetAttribute("CompositionName", "RangeValues");
        var rangeOnAttr = CreateElement("AttributeList");
        AddAttribute(rangeOnAttr, "BackColor", onColor);
        AddAttribute(rangeOnAttr, "FlashingType", "No");
        AddAttribute(rangeOnAttr, "ForeColor", "0,0,0");
        AddAttribute(rangeOnAttr, "LowerLimit", "1");
        AddAttribute(rangeOnAttr, "UpperLimit", "1");
        rangeOn.AppendChild(rangeOnAttr);
        animObj.AppendChild(rangeOn);

        anim.AppendChild(animObj);
        objList.AppendChild(anim);
        circle.AppendChild(objList);

        return circle;
    }

    /// <summary>
    /// V17-compatible status lamp: a visible base circle plus a second circle
    /// that is shown by a single-bit visibility animation. The caller places
    /// both returned elements directly in the screen layer.
    /// </summary>
    public (XmlElement Base, XmlElement Active) CreateV17StatusLamp(
        string name, int left, int top, int radius, string tagName,
        string offColor = "173, 174, 181", string onColor = "0, 200, 0")
    {
        var baseLamp = CreateElement("Hmi.Screen.Circle");
        baseLamp.SetAttribute("ID", NextId());
        baseLamp.SetAttribute("CompositionName", "ScreenItems");
        var baseAttr = CreateElement("AttributeList");
        AddAttribute(baseAttr, "BackColor", offColor);
        AddAttribute(baseAttr, "BackFillStyle", "Solid");
        AddAttribute(baseAttr, "BorderColor", "24, 28, 49");
        AddAttribute(baseAttr, "BorderWidth", "1");
        AddAttribute(baseAttr, "EdgeStyle", "Solid");
        AddAttribute(baseAttr, "Flashing", "None");
        AddAttribute(baseAttr, "Height", (radius * 2).ToString());
        AddAttribute(baseAttr, "Left", left.ToString());
        AddAttribute(baseAttr, "ObjectName", name + "_底灯");
        AddAttribute(baseAttr, "Radius", radius.ToString());
        AddAttribute(baseAttr, "TabIndex", "-1");
        AddAttribute(baseAttr, "Top", top.ToString());
        AddAttribute(baseAttr, "UseDesignColorSchema", "false");
        AddAttribute(baseAttr, "Width", (radius * 2).ToString());
        baseLamp.AppendChild(baseAttr);

        var activeLamp = CreateElement("Hmi.Screen.Circle");
        activeLamp.SetAttribute("ID", NextId());
        activeLamp.SetAttribute("CompositionName", "ScreenItems");
        var activeAttr = CreateElement("AttributeList");
        AddAttribute(activeAttr, "BackColor", onColor);
        AddAttribute(activeAttr, "BackFillStyle", "Solid");
        AddAttribute(activeAttr, "BorderColor", "24, 28, 49");
        AddAttribute(activeAttr, "BorderWidth", "1");
        AddAttribute(activeAttr, "EdgeStyle", "Solid");
        AddAttribute(activeAttr, "Flashing", "None");
        AddAttribute(activeAttr, "Height", (radius * 2).ToString());
        AddAttribute(activeAttr, "Left", left.ToString());
        AddAttribute(activeAttr, "ObjectName", name);
        AddAttribute(activeAttr, "Radius", radius.ToString());
        AddAttribute(activeAttr, "TabIndex", "-1");
        AddAttribute(activeAttr, "Top", top.ToString());
        AddAttribute(activeAttr, "UseDesignColorSchema", "false");
        AddAttribute(activeAttr, "Width", (radius * 2).ToString());
        activeLamp.AppendChild(activeAttr);

        var objectList = CreateElement("ObjectList");
        var animation = CreateElement("Hmi.Dynamic.SingleBitVisibilityAnimation");
        animation.SetAttribute("ID", NextId());
        animation.SetAttribute("CompositionName", "Animations");
        var animationAttr = CreateElement("AttributeList");
        AddAttribute(animationAttr, "BitPosition", "0");
        AddAttribute(animationAttr, "Name", "VisibilityAnimation");
        AddAttribute(animationAttr, "Visible", "true");
        animation.AppendChild(animationAttr);
        var animationObjects = CreateElement("ObjectList");
        var trigger = CreateElement("Hmi.Dynamic.TagElementTrigger");
        trigger.SetAttribute("ID", NextId());
        trigger.SetAttribute("CompositionName", "VisibilityTag");
        var links = CreateElement("LinkList");
        links.AppendChild(CreateTagLink(tagName));
        trigger.AppendChild(links);
        animationObjects.AppendChild(trigger);
        animation.AppendChild(animationObjects);
        objectList.AppendChild(animation);
        activeLamp.AppendChild(objectList);

        return (baseLamp, activeLamp);
    }

    public XmlElement CreateLine(
        string name, int startLeft, int startTop, int endLeft, int endTop,
        string color = "0,0,0", int lineWidth = 1, string style = "Solid",
        string startStyle = "NoEnd", string endStyle = "NoEnd")
    {
        if (lineWidth < 1 || lineWidth > 20) throw new InvalidOperationException("LineWidth 必须在 1..20。");
        var line = CreateElement("Hmi.Screen.Line");
        line.SetAttribute("ID", NextId());
        line.SetAttribute("CompositionName", "ScreenItems");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "Color", color);
        AddAttribute(attr, "EndLeft", endLeft.ToString());
        AddAttribute(attr, "EndStyle", endStyle);
        AddAttribute(attr, "EndTop", endTop.ToString());
        AddAttribute(attr, "FillStyle", "Transparent");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "Height", Math.Abs(endTop - startTop).ToString());
        AddAttribute(attr, "Left", Math.Min(startLeft, endLeft).ToString());
        AddAttribute(attr, "LineEndShapeStyle", "Round");
        AddAttribute(attr, "LineWidth", lineWidth.ToString());
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "StartLeft", startLeft.ToString());
        AddAttribute(attr, "StartStyle", startStyle);
        AddAttribute(attr, "StartTop", startTop.ToString());
        AddAttribute(attr, "Style", style);
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "Top", Math.Min(startTop, endTop).ToString());
        AddAttribute(attr, "UseDesignColorSchema", "false");
        AddAttribute(attr, "Width", Math.Abs(endLeft - startLeft).ToString());
        line.AppendChild(attr);
        return line;
    }

    public XmlElement CreateGroup(string name, IEnumerable<XmlElement> members)
    {
        var list = members?.Where(x => x != null).ToList() ?? new List<XmlElement>();
        if (list.Count == 0) throw new InvalidOperationException($"Group '{name}' 至少需要一个成员。");
        var group = CreateElement("Hmi.Screen.Group");
        group.SetAttribute("ID", NextId());
        group.SetAttribute("CompositionName", "ScreenItems");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "TabIndex", "-1");
        group.AppendChild(attr);
        var obj = CreateElement("ObjectList");
        foreach (var member in list) obj.AppendChild(_doc.ImportNode(member, true));
        group.AppendChild(obj);
        return group;
    }

    public XmlElement CreateSymbolicIoField(
        string name, int left, int top, int width, int height,
        string tagName, string textListName, string mode = "Output",
        string backColor = "255,255,255", string foreColor = "49,52,74",
        string fontSize = "16", string fontStyle = "Bold", bool showDropDown = false,
        string textOff = "0", string textOn = "1")
    {
        var field = CreateElement("Hmi.Screen.SymbolicIOField");
        field.SetAttribute("ID", NextId());
        field.SetAttribute("CompositionName", "ScreenItems");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "AboveUpperLimitColor", "237,88,97");
        AddAttribute(attr, "BackColor", backColor);
        AddAttribute(attr, "BackFillStyle", "Solid");
        AddAttribute(attr, "BelowLowerLimitColor", "241,161,44");
        AddAttribute(attr, "BitNumber", "0");
        AddAttribute(attr, "BorderBackColor", "101,103,115");
        AddAttribute(attr, "BorderColor", "71,73,87");
        AddAttribute(attr, "BorderWidth", "2");
        AddAttribute(attr, "BottomMargin", "2");
        AddAttribute(attr, "CornerRadius", "3");
        AddAttribute(attr, "CountVisibleItems", "3");
        AddAttribute(attr, "EdgeStyle", "Double");
        AddAttribute(attr, "Enabled", "true");
        AddAttribute(attr, "EvenRowBackColor", "230,230,232");
        AddAttribute(attr, "FitToLargest", "false");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "FlashingOnLimitViolation", "false");
        AddAttribute(attr, "ForeColor", foreColor);
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "HorizontalAlignment", "Center");
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "LeftMargin", "3");
        AddAttribute(attr, "Mode", mode);
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "OnValue", "1");
        AddAttribute(attr, "RightMargin", "2");
        AddAttribute(attr, "SelectBackColor", "0,0,0");
        AddAttribute(attr, "SelectForeColor", "255,255,255");
        AddAttribute(attr, "ShowDropDownButton", showDropDown ? "true" : "false");
        AddAttribute(attr, "ShowDropDownList", showDropDown ? "true" : "false");
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "TextOrientation", "Horizontal");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "TopMargin", "2");
        AddAttribute(attr, "UseDesignColorSchema", "false");
        AddAttribute(attr, "VerticalAlignment", "Middle");
        AddAttribute(attr, "Width", width.ToString());
        field.AppendChild(attr);
        var links = CreateElement("LinkList");
        links.AppendChild(CreateTextListLink(textListName));
        field.AppendChild(links);
        var obj = CreateElement("ObjectList");
        obj.AppendChild(CreateFont("宋体", fontSize, fontStyle));
        obj.AppendChild(CreateMultilingualText("HelpText"));
        obj.AppendChild(CreateTagProperty("ProcessValue", tagName));
        obj.AppendChild(CreateMultilingualText("TextOff", textOff));
        obj.AppendChild(CreateMultilingualText("TextOn", textOn));
        field.AppendChild(obj);
        return field;
    }

    public XmlElement CreateGraphicView(
        string name, int left, int top, int width, int height, string pictureName,
        string autoSizing = "StretchPicture", bool useTransparentColor = false,
        string transparentColor = "255,0,255")
    {
        var view = CreateElement("Hmi.Screen.GraphicView");
        view.SetAttribute("ID", NextId());
        view.SetAttribute("CompositionName", "ScreenItems");
        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "AutoSizing", autoSizing);
        AddAttribute(attr, "BackColor", "173,174,181");
        AddAttribute(attr, "BackFillStyle", "Solid");
        AddAttribute(attr, "BorderColor", "0,0,0");
        AddAttribute(attr, "BorderWidth", "0");
        AddAttribute(attr, "EdgeStyle", "Solid");
        AddAttribute(attr, "FitToLargest", "false");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "TransparentColor", transparentColor);
        AddAttribute(attr, "UseTransparentColor", useTransparentColor ? "true" : "false");
        AddAttribute(attr, "Width", width.ToString());
        view.AppendChild(attr);
        var links = CreateElement("LinkList");
        links.AppendChild(CreatePictureLink(pictureName));
        view.AppendChild(links);
        return view;
    }

    /// <summary>
    /// 创建文本标签控件
    /// </summary>
    public XmlElement CreateTextLabel(
        string name, int left, int top, int width, int height,
        string text, string fontSize = "14", bool transparent = true,
        string foreColor = "49,52,74")
    {
        var tf = CreateElement("Hmi.Screen.TextField");
        tf.SetAttribute("ID", NextId());
        tf.SetAttribute("CompositionName", "ScreenItems");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "BackColor", "255,255,255");
        AddAttribute(attr, "BackFillStyle", transparent ? "Transparent" : "Solid");
        AddAttribute(attr, "BorderBackColor", "101,103,115");
        AddAttribute(attr, "BorderColor", "71,73,87");
        AddAttribute(attr, "BorderWidth", "0");
        AddAttribute(attr, "BottomMargin", "2");
        AddAttribute(attr, "CornerRadius", "3");
        AddAttribute(attr, "EdgeStyle", "Double");
        AddAttribute(attr, "FitToLargest", transparent ? "true" : "false");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "ForeColor", foreColor);
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "HorizontalAlignment", "Left");
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "LeftMargin", "3");
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "RightMargin", "2");
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "TextOrientation", "Horizontal");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "TopMargin", "2");
        AddAttribute(attr, "UseDesignColorSchema", "false");
        AddAttribute(attr, "VerticalAlignment", "Middle");
        AddAttribute(attr, "Width", width.ToString());
        tf.AppendChild(attr);

        var objList = CreateElement("ObjectList");
        objList.AppendChild(CreateFont("宋体", fontSize, "Regular"));
        objList.AppendChild(CreateMultilingualText("Text", text));
        tf.AppendChild(objList);

        return tf;
    }

    /// <summary>
    /// 创建按钮控件。支持多种事件类型。
    /// eventType: "invertBit"(默认) | "setBit"(Press→置位) | "resetBit"(Click→复位) | 
    ///            "impulse"(Press→置位+Release→复位) | "activateScreen"(Click→切换画面)
    /// eventTarget: 位操作时为变量名，画面切换时为目标画面名
    /// </summary>
    public XmlElement CreateButton(
        string name, int left, int top, int width, int height,
        string text, string tagName, string backColor = "70,130,220",
        string foreColor = "255,255,255", string fontSize = "14", string fontStyle = "Bold",
        string eventType = "invertBit", string? eventTarget = null)
    {
        var btn = CreateElement("Hmi.Screen.Button");
        btn.SetAttribute("ID", NextId());
        btn.SetAttribute("CompositionName", "ScreenItems");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "BackColor", backColor);
        AddAttribute(attr, "BackFillStyle", "Solid");
        AddAttribute(attr, "BitNumber", "0");
        AddAttribute(attr, "BorderBackColor", "105,105,105");
        AddAttribute(attr, "BorderColor", "30,80,160");
        AddAttribute(attr, "BorderWidth", "2");
        AddAttribute(attr, "CornerRadius", "5");
        AddAttribute(attr, "CornerStyle", "Pointed");
        AddAttribute(attr, "EdgeStyle", "Solid");
        AddAttribute(attr, "Enabled", "true");
        AddAttribute(attr, "FirstGradientColor", backColor);
        AddAttribute(attr, "FirstGradientOffset", "15");
        AddAttribute(attr, "FitToLargest", "false");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "FocusColor", "148,182,231");
        AddAttribute(attr, "FocusWidth", "2");
        AddAttribute(attr, "ForeColor", foreColor);
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "HorizontalAlignment", "Center");
        AddAttribute(attr, "HorizontalPictureAlignment", "Center");
        AddAttribute(attr, "HotKey", "0");
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "MiddleGradientColor", backColor);
        AddAttribute(attr, "Mode", "Text");
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "PictureAreaBottomMargin", "0");
        AddAttribute(attr, "PictureAreaLeftMargin", "0");
        AddAttribute(attr, "PictureAreaRightMargin", "0");
        AddAttribute(attr, "PictureAreaTopMargin", "0");
        AddAttribute(attr, "PictureAutoSizing", "StretchPicture");
        AddAttribute(attr, "SecondGradientColor", backColor);
        AddAttribute(attr, "SecondGradientOffset", "15");
        AddAttribute(attr, "TabIndex", _buttonTabIndex++.ToString());
        AddAttribute(attr, "TextAreaBottomMargin", "0");
        AddAttribute(attr, "TextAreaLeftMargin", "0");
        AddAttribute(attr, "TextAreaRightMargin", "0");
        AddAttribute(attr, "TextAreaTopMargin", "0");
        AddAttribute(attr, "TextOrientation", "Horizontal");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "UseDesignColorSchema", "false");
        AddAttribute(attr, "UseFirstGradient", "true");
        AddAttribute(attr, "UseSecondGradient", "true");
        AddAttribute(attr, "UseTwoHandOperation", "false");
        AddAttribute(attr, "VerticalAlignment", "Middle");
        AddAttribute(attr, "VerticalPictureAlignment", "Middle");
        AddAttribute(attr, "Width", width.ToString());
        btn.AppendChild(attr);

        var objList = CreateElement("ObjectList");
        var target = eventTarget ?? tagName;

        switch (eventType)
        {
            case "setBit":
                objList.AppendChild(CreateButtonEvent("Press", "SetBit", target));
                break;
            case "resetBit":
                objList.AppendChild(CreateButtonEvent("Click", "ResetBit", target));
                break;
            case "impulse":
                objList.AppendChild(CreateButtonEvent("Press", "SetBit", target));
                objList.AppendChild(CreateButtonEvent("Release", "ResetBit", target));
                break;
            case "activateScreen":
                objList.AppendChild(CreateButtonEvent("Click", new[] { CreateActivateScreenAction("Click", target) }));
                break;
            case "none":
                break;
            case "invertBit":
                objList.AppendChild(CreateButtonEvent("Click", "InvertBit", target));
                break;
            default:
                throw new InvalidOperationException($"不支持的按钮事件类型 '{eventType}'。已拒绝静默降级为 InvertBit。");
        }

        objList.AppendChild(CreateFont("宋体", fontSize, fontStyle));
        objList.AppendChild(CreateMultilingualText("HelpText"));
        objList.AppendChild(CreateMultilingualText("TextOff", text));
        // V17 displays TextOn when the button is active; do not leave the English template placeholder.
        objList.AppendChild(CreateMultilingualText("TextOn", text));

        btn.AppendChild(objList);
        return btn;
    }

    public XmlElement CreateButtonAdvanced(
        string name, int left, int top, int width, int height, string text,
        IEnumerable<HmiButtonActionSpec> actions, string backColor = "70,130,220",
        string foreColor = "255,255,255", string fontSize = "14", string fontStyle = "Bold")
    {
        var btn = CreateButton(name, left, top, width, height, text, "", backColor, foreColor, fontSize, fontStyle, "none", "");
        var objectList = btn["ObjectList"];
        if (objectList == null) return btn;
        var oldEvents = objectList.ChildNodes.Cast<XmlNode>().Where(x => x.Name == "Hmi.Event.Event").ToList();
        foreach (var old in oldEvents) objectList.RemoveChild(old);
        foreach (var group in actions.Where(a => a != null).GroupBy(a => string.IsNullOrWhiteSpace(a.Trigger) ? "Click" : a.Trigger, StringComparer.OrdinalIgnoreCase))
            objectList.PrependChild(CreateButtonEvent(group.Key, group));
        return btn;
    }

    /// <summary>创建按钮的单个事件（Press/Release/Click/SwitchOn/SwitchOff）</summary>
    private XmlElement CreateButtonEvent(string triggerName, string funcName, string targetValue, string paramName = "Tag")
        => CreateButtonEvent(triggerName, new[]
        {
            new HmiButtonActionSpec
            {
                Trigger = triggerName,
                Function = funcName,
                Target = targetValue,
                ParameterName = paramName,
                Parameters = new List<HmiFunctionParameterSpec>
                {
                    new HmiFunctionParameterSpec { Name = paramName, Value = targetValue, IsLink = true }
                }
            }
        });

    private static HmiButtonActionSpec CreateActivateScreenAction(string triggerName, string screenName)
        => new HmiButtonActionSpec
        {
            Trigger = triggerName,
            Function = "ActivateScreen",
            Target = screenName,
            Parameters = new List<HmiFunctionParameterSpec>
            {
                new HmiFunctionParameterSpec { Name = "Screen name", Value = screenName, IsLink = true },
                new HmiFunctionParameterSpec { Name = "Object number", Value = "0", IsLink = false, Type = "System.Int32" }
            }
        };

    private static IReadOnlyList<HmiFunctionParameterSpec> ResolveActionParameters(HmiButtonActionSpec action)
        => HmiActionCatalog.ResolveParameters(action);

    private static string CanonicalHmiTrigger(string triggerName)
        => HmiActionCatalog.CanonicalTrigger(triggerName);

    private static string CanonicalHmiFunction(string functionName)
        => HmiActionCatalog.CanonicalFunction(functionName);

    private XmlElement CreateButtonEvent(string triggerName, IEnumerable<HmiButtonActionSpec> actions)
    {
        var evt = CreateElement("Hmi.Event.Event");
        evt.SetAttribute("ID", NextId());
        evt.SetAttribute("CompositionName", "Events");
        var evtAttr = CreateElement("AttributeList");
        AddAttribute(evtAttr, "Name", CanonicalHmiTrigger(triggerName));
        evt.AppendChild(evtAttr);

        var evtObj = CreateElement("ObjectList");
        var handler = CreateElement("Hmi.Event.FunctionListEventHandler");
        handler.SetAttribute("ID", NextId());
        handler.SetAttribute("CompositionName", "EventHandler");
        var handlerObj = CreateElement("ObjectList");

        foreach (var action in actions)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Function)) continue;
            HmiActionCatalog.Validate(action);
            var function = CanonicalHmiFunction(action.Function);
            var parameters = ResolveActionParameters(action);
            if (parameters.Count == 0 || parameters.Any(p => string.IsNullOrWhiteSpace(p.Name) || string.IsNullOrWhiteSpace(p.Value)))
                throw new InvalidOperationException($"HMI 系统函数 {function} 的参数不完整。");

            var entry = CreateElement("Hmi.Event.FunctionListEntry");
            entry.SetAttribute("ID", NextId());
            entry.SetAttribute("CompositionName", "FunctionListEntries");
            var entryAttr = CreateElement("AttributeList");
            AddAttribute(entryAttr, "Name", function);
            AddAttribute(entryAttr, "Type", "SystemFunction");
            entry.AppendChild(entryAttr);

            var entryObj = CreateElement("ObjectList");
            foreach (var parameter in parameters)
            {
                var param = CreateElement("Hmi.Event.FunctionListEntryParameter");
                param.SetAttribute("ID", NextId());
                param.SetAttribute("CompositionName", "Parameters");
                var paramAttr = CreateElement("AttributeList");
                AddAttribute(paramAttr, "Name", parameter.Name);
                if (parameter.IsLink)
                {
                    param.AppendChild(paramAttr);
                    var paramLink = CreateElement("LinkList");
                    paramLink.AppendChild(CreateValueLink(parameter.Value));
                    param.AppendChild(paramLink);
                }
                else
                {
                    var value = CreateElement("Value");
                    value.SetAttribute("Type", string.IsNullOrWhiteSpace(parameter.Type) ? "System.String" : parameter.Type);
                    value.InnerText = parameter.Value;
                    paramAttr.AppendChild(value);
                    param.AppendChild(paramAttr);
                }
                entryObj.AppendChild(param);
            }
            entry.AppendChild(entryObj);
            handlerObj.AppendChild(entry);
        }
        handler.AppendChild(handlerObj);
        evtObj.AppendChild(handler);
        evt.AppendChild(evtObj);
        return evt;
    }

    /// <summary>
    /// 创建开关控件（SwitchOff→ResetBit + SwitchOn→SetBit）
    /// </summary>
    public XmlElement CreateSwitch(
        string name, int left, int top, int width, int height,
        string tagName, string textOff = "OFF", string textOn = "ON",
        string offColor = "255,100,100", string onColor = "100,180,100",
        string caption = "")
    {
        var sw = CreateElement("Hmi.Screen.Switch");
        sw.SetAttribute("ID", NextId());
        sw.SetAttribute("CompositionName", "ScreenItems");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "BackColor", offColor);
        AddAttribute(attr, "BorderColor", "80,80,80");
        AddAttribute(attr, "BorderWidth", "1");
        AddAttribute(attr, "ForeColor", "255,255,255");
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "HorizontalAlignment", "Center");
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "ShowCaption", (string.IsNullOrEmpty(caption) ? "false" : "true"));
        AddAttribute(attr, "SwitchOrientation", "LeftToRight");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "VerticalAlignment", "Middle");
        AddAttribute(attr, "Width", width.ToString());
        sw.AppendChild(attr);

        var objList = CreateElement("ObjectList");
        if (!string.IsNullOrEmpty(caption))
        {
            objList.AppendChild(CreateFont("宋体", "16", "Regular", "CaptionFont"));
            objList.AppendChild(CreateMultilingualText("CaptionText", caption, "zh-CN", richText: false));
        }
        objList.AppendChild(CreateButtonEvent("SwitchOn", "SetBit", tagName));
        objList.AppendChild(CreateButtonEvent("SwitchOff", "ResetBit", tagName));
        // ★修复★ switch 必须绑定过程值（ProcessValue），否则 HMI 编译报"过程变量丢失"
        objList.AppendChild(CreateProcessValueProperty(tagName));

        objList.AppendChild(CreateFont("宋体", "14", "Bold"));
        objList.AppendChild(CreateMultilingualText("TextOff", textOff));
        objList.AppendChild(CreateMultilingualText("TextOn", textOn));

        sw.AppendChild(objList);
        return sw;
    }

    /// <summary>
    /// 创建过程值绑定属性（Hmi.Screen.Property + TagConnectionDynamic），
    /// 与 TIA 导出 IO 域/开关的 ProcessValue 结构一致，用于消除"过程变量丢失"编译警告。
    /// </summary>
    private XmlElement CreateProcessValueProperty(string tagName)
    {
        var prop = CreateElement("Hmi.Screen.Property");
        prop.SetAttribute("ID", NextId());
        prop.SetAttribute("CompositionName", "Properties");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "Name", "ProcessValue");
        prop.AppendChild(attr);

        var objList = CreateElement("ObjectList");
        var dyn = CreateElement("Hmi.Dynamic.TagConnectionDynamic");
        dyn.SetAttribute("ID", NextId());
        dyn.SetAttribute("CompositionName", "Dynamic");
        var dynAttr = CreateElement("AttributeList");
        AddAttribute(dynAttr, "Indirect", "false");
        dyn.AppendChild(dynAttr);
        var ll = CreateElement("LinkList");
        var tag = CreateElement("Tag");
        tag.SetAttribute("TargetID", "@OpenLink");
        AddAttribute(tag, "Name", tagName);
        ll.AppendChild(tag);
        dyn.AppendChild(ll);
        objList.AppendChild(dyn);
        prop.AppendChild(objList);
        return prop;
    }

    /// <summary>
    /// 创建IO域控件（输出/输入域，绑定过程值）
    /// </summary>
    public XmlElement CreateIoField(
        string name, int left, int top, int width, int height,
        string tagName, string mode = "Output", string formatPattern = "999",
        string backColor = "0,0,0", string foreColor = "0,255,0",
        string fontSize = "36", string fontStyle = "Bold", int fieldLength = 3)
    {
        var iof = CreateElement("Hmi.Screen.IOField");
        iof.SetAttribute("ID", NextId());
        iof.SetAttribute("CompositionName", "ScreenItems");

        var attr = CreateElement("AttributeList");
        AddAttribute(attr, "AboveUpperLimitColor", "237,88,97");
        AddAttribute(attr, "BackColor", backColor);
        AddAttribute(attr, "BackFillStyle", "Solid");
        AddAttribute(attr, "BelowLowerLimitColor", "241,161,44");
        AddAttribute(attr, "BorderBackColor", "101,103,115");
        AddAttribute(attr, "BorderColor", "50,50,50");
        AddAttribute(attr, "BorderWidth", "3");
        AddAttribute(attr, "BottomMargin", "2");
        AddAttribute(attr, "CornerRadius", "3");
        AddAttribute(attr, "DataFormat", "Decimal");
        AddAttribute(attr, "EdgeStyle", "Solid");
        AddAttribute(attr, "Enabled", "true");
        AddAttribute(attr, "FieldLength", fieldLength.ToString());
        AddAttribute(attr, "FitToLargest", "false");
        AddAttribute(attr, "Flashing", "None");
        AddAttribute(attr, "ForeColor", foreColor);
        AddAttribute(attr, "FormatPattern", formatPattern);
        AddAttribute(attr, "Height", height.ToString());
        AddAttribute(attr, "HiddenInput", "false");
        AddAttribute(attr, "HorizontalAlignment", "Center");
        AddAttribute(attr, "Left", left.ToString());
        AddAttribute(attr, "LeftMargin", "3");
        AddAttribute(attr, "Mode", mode);
        AddAttribute(attr, "ObjectName", name);
        AddAttribute(attr, "RightMargin", "2");
        AddAttribute(attr, "ShiftDecimalPoint", "0");
        AddAttribute(attr, "ShowLeadingZeros", "false");
        AddAttribute(attr, "TabIndex", "-1");
        AddAttribute(attr, "TextOrientation", "Horizontal");
        AddAttribute(attr, "Top", top.ToString());
        AddAttribute(attr, "TopMargin", "2");
        AddAttribute(attr, "Unit", "");
        AddAttribute(attr, "UseDesignColorSchema", "false");
        AddAttribute(attr, "UseTwoHandOperation", "false");
        AddAttribute(attr, "VerticalAlignment", "Middle");
        AddAttribute(attr, "Width", width.ToString());
        iof.AppendChild(attr);

        var objList = CreateElement("ObjectList");
        objList.AppendChild(CreateFont("宋体", fontSize, fontStyle));

        objList.AppendChild(CreateTagProperty("ProcessValue", tagName));

        iof.AppendChild(objList);
        return iof;
    }

    /// <summary>
    /// 构建完整的画面XML文档
    /// </summary>
    public string BuildScreenDocument(
        string screenName,
        int width, int height,
        List<XmlElement> screenItems,
        string backColor = "240,240,240",
        string engineeringVersion = "",
        int screenNumber = 1,
        IReadOnlyDictionary<int, List<XmlElement>>? layerItems = null,
        IReadOnlyDictionary<int, string>? layerNames = null,
        string templateName = "")
    {
        if (string.IsNullOrWhiteSpace(engineeringVersion))
            engineeringVersion = EnvironmentDiscoveryService.CurrentEngineeringVersion();
        var doc = new XmlDocument();

        var decl = doc.CreateXmlDeclaration("1.0", "utf-8", null);
        doc.AppendChild(decl);

        var documentElem = doc.CreateElement("Document");
        doc.AppendChild(documentElem);

        var engineering = doc.CreateElement("Engineering");
        engineering.SetAttribute("version", engineeringVersion);
        documentElem.AppendChild(engineering);

        // DocumentInfo
        var created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var docInfo = doc.CreateElement("DocumentInfo");
        var createdElem = doc.CreateElement("Created");
        createdElem.InnerText = created;
        docInfo.AppendChild(createdElem);
        var exportSetting = doc.CreateElement("ExportSetting");
        exportSetting.InnerText = "WithDefaults";
        docInfo.AppendChild(exportSetting);
        var instProds = doc.CreateElement("InstalledProducts");
        // V17 导出为 WinCC Professional；V18+ 含 WinCC Basic/Comfort/Advanced 与 WinCC Unified
        string[] prods;
        if (engineeringVersion.StartsWith("V17", StringComparison.OrdinalIgnoreCase))
        {
            prods = new[] {
                "Totally Integrated Automation Portal",
                "TIA Portal Openness",
                "TIA Portal Version Control Interface",
                "STEP 7 Professional",
                "STEP 7 Safety",
                "WinCC Professional"
            };
        }
        else
        {
            prods = new[] {
                "Totally Integrated Automation Portal",
                "TIA Portal Openness",
                "TIA Portal Version Control Interface",
                "STEP 7 Professional",
                "STEP 7 Safety",
                "WinCC Basic/Comfort/Advanced",
                "WinCC Unified"
            };
        }
        foreach (var p in prods)
        {
            var pe = doc.CreateElement(p.Contains("Openness") || p.Contains("Version Control") || p.Contains("Safety") ? "OptionPackage" : "Product");
            var dpn = doc.CreateElement("DisplayName"); dpn.InnerText = p; pe.AppendChild(dpn);
            var dpv = doc.CreateElement("DisplayVersion"); dpv.InnerText = engineeringVersion; pe.AppendChild(dpv);
            instProds.AppendChild(pe);
        }
        docInfo.AppendChild(instProds);
        documentElem.AppendChild(docInfo);

        var screenElem = doc.CreateElement("Hmi.Screen.Screen");
        screenElem.SetAttribute("ID", "0");
        documentElem.AppendChild(screenElem);

        var scrAttr = doc.CreateElement("AttributeList");
        AddAttribute(scrAttr, "ActiveLayer", "0");
        AddAttribute(scrAttr, "BackColor", backColor);
        AddAttribute(scrAttr, "GridColor", "0, 0, 0");
        AddAttribute(scrAttr, "Height", height.ToString());
        AddAttribute(scrAttr, "Name", screenName);
        AddAttribute(scrAttr, "Number", screenNumber.ToString());
        AddAttribute(scrAttr, "Visible", "true");
        AddAttribute(scrAttr, "Width", width.ToString());
        screenElem.AppendChild(scrAttr);

        // ★V17 校准：Screen 的 AttributeList 之后必须跟 LinkList/Template，
        // 否则 V17 导入器会因缺少画面模板引用而拒绝导入。
        // ★修复★ 模板名空（设备无默认模板，如部分 V17 面板 ScreenTemplateFolder 为空）时不输出
        // LinkList/Template——由 TIA 导入时自动分配设备默认模板。硬编码“模板_1”会
        // 导致编译报 "Invalid template for screen"。与 GenerateEmptyScreenXml 语义一致。
        if (!string.IsNullOrWhiteSpace(templateName))
        {
            var scrLink = doc.CreateElement("LinkList");
            var template = doc.CreateElement("Template");
            template.SetAttribute("TargetID", "@OpenLink");
            var templateNameElem = doc.CreateElement("Name");
            templateNameElem.InnerText = templateName;
            template.AppendChild(templateNameElem);
            scrLink.AppendChild(template);
            screenElem.AppendChild(scrLink);
        }

        var scrObj = doc.CreateElement("ObjectList");

        var helpText = _doc.CreateElement("MultilingualText");
        helpText.SetAttribute("ID", NextId());
        helpText.SetAttribute("CompositionName", "HelpText");
        var htObjList = _doc.CreateElement("ObjectList");
        var htItem = _doc.CreateElement("MultilingualTextItem");
        htItem.SetAttribute("ID", NextId());
        htItem.SetAttribute("CompositionName", "Items");
        var htItemAttr = _doc.CreateElement("AttributeList");
        var htCulture = _doc.CreateElement("Culture"); htCulture.InnerText = "zh-CN"; htItemAttr.AppendChild(htCulture);
        var htText = _doc.CreateElement("Text"); htItemAttr.AppendChild(htText);
        htItem.AppendChild(htItemAttr);
        htObjList.AppendChild(htItem);
        helpText.AppendChild(htObjList);
        scrObj.AppendChild(doc.ImportNode(helpText, true));

        var layers = layerItems ?? new Dictionary<int, List<XmlElement>> { [0] = screenItems };
        foreach (var pair in layers.OrderBy(x => x.Key))
        {
            if (pair.Key < 0 || pair.Key > 31)
                throw new InvalidOperationException($"HMI 画面层索引 {pair.Key} 超出支持范围 0..31。");
            var layer = doc.CreateElement("Hmi.Screen.ScreenLayer");
            layer.SetAttribute("ID", NextId());
            layer.SetAttribute("CompositionName", "Layers");
            var layerAttr = doc.CreateElement("AttributeList");
            AddAttribute(layerAttr, "Index", pair.Key.ToString());
            AddAttribute(layerAttr, "Name", layerNames != null && layerNames.TryGetValue(pair.Key, out var layerName) ? layerName : "");
            AddAttribute(layerAttr, "VisibleES", "true");
            layer.AppendChild(layerAttr);

            var layerObj = doc.CreateElement("ObjectList");
            foreach (var item in pair.Value)
                layerObj.AppendChild(doc.ImportNode(item, true));
            layer.AppendChild(layerObj);
            scrObj.AppendChild(layer);
        }

        screenElem.AppendChild(scrObj);

        var sb = new StringBuilder();
        // ★修复★ StringWriter.Encoding 固定为 UTF-16，XmlWriter 会把声明写成
        // encoding="utf-16"，而落盘用 UTF-8+BOM —— 声明与实际编码不一致会被
        // System.Xml 系导入器拒绝（无模板回退路径全部失败）。用可覆盖编码的
        // StringWriter 让声明与文件编码一致。
        using (var sw = new Utf8StringWriter(sb))
        using (var xw = XmlWriter.Create(sw, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(true),
            OmitXmlDeclaration = false
        }))
        {
            doc.WriteTo(xw);
        }

        return sb.ToString();
    }

    /// <summary>允许 XmlWriter 在字符串输出中声明 utf-8（StringWriter 默认强制 UTF-16）。</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        private readonly Encoding _encoding;
        public Utf8StringWriter(StringBuilder sb, Encoding? encoding = null) : base(sb)
        {
            _encoding = encoding ?? new UTF8Encoding(false);
        }
        public override Encoding Encoding => _encoding;
    }
}
