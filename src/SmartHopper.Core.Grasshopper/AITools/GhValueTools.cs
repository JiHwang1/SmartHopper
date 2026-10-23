/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GhJSON.Core;
using GhJSON.Grasshopper;
using GhJSON.Grasshopper.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Native Grasshopper value and pulse operations for B02 tools.</summary>
    internal static class GhValueTools
    {
        internal enum ValueResult
        {
            Success,
            NotFound,
            Unsupported,
            Failed,
        }

        internal static string Signature(GH_Document document, IEnumerable<Guid> ids)
        {
            var requested = ids.ToHashSet();
            var objects = document.Objects
                .OfType<IGH_DocumentObject>()
                .Where(obj => requested.Contains(obj.InstanceGuid));
            return GhJson.ToJson(GhJsonGrasshopper.Serialize(objects, new SerializationOptions
            {
                IncludeConnections = false,
                IncludeGroups = false,
                IncludeInternalizedData = true,
                IncludeRuntimeData = false,
                IncludeRuntimeMessages = false,
                IncludeSelectedState = false,
                IncludeMetadata = false,
            }));
        }

        internal static IGH_DocumentObject? Find(GH_Document document, Guid guid)
        {
            return document.Objects.OfType<IGH_DocumentObject>()
                .FirstOrDefault(obj => obj.InstanceGuid == guid);
        }

        internal static bool IsProtected(GH_Document document, Guid guid)
        {
            return CanvasProtection.IsProtected(guid);
        }

        internal static (ValueResult Result, string Detail) SetValue(
            GH_Document document,
            Guid guid,
            JToken value,
            string? paramName = null)
        {
            if (document.SolutionDepth > 0)
            {
                return (ValueResult.Failed, "A Grasshopper solution is in progress; the value was not applied.");
            }

            var target = Find(document, guid);
            if (target == null)
            {
                return (ValueResult.NotFound, $"No object in the reviewed document matches {guid}.");
            }
            if (target is IGH_ActiveObject locked && locked.Locked)
                return (ValueResult.Failed, "Object is locked; unlock it before setting its value.");

            IGH_DocumentObject undoOwner = target;
            IGH_Param? persistentTarget = null;
            if (!string.IsNullOrWhiteSpace(paramName))
            {
                if (target is not IGH_Component component)
                {
                    return (ValueResult.Unsupported, "The 'param' argument only applies to components.");
                }

                persistentTarget = ResolveInputParam(component, paramName);
                if (persistentTarget == null)
                {
                    return (ValueResult.NotFound, $"Component '{component.NickName}' has no input parameter matching '{paramName}'.");
                }

                undoOwner = persistentTarget;
            }

            undoOwner.RecordUndoEvent("[SH] Set Value");
            ValueResult result;
            string detail;
            if (persistentTarget != null)
            {
                var applied = SetPersistentParamValue(persistentTarget, value, out detail);
                result = applied ? ValueResult.Success : ValueResult.Failed;
            }
            else if (!TryApplyObjectValue(target, value, out result, out detail))
            {
                return (result, detail);
            }

            if (result == ValueResult.Success)
            {
                if (target is IGH_ActiveObject active) active.ExpireSolution(false);
                document.NewSolution(false);
                Instances.RedrawCanvas();
            }

            return (result, detail);
        }

        private static bool TryApplyObjectValue(
            IGH_DocumentObject obj,
            JToken value,
            out ValueResult result,
            out string detail)
        {
            switch (obj)
            {
                case GH_Panel panel:
                    panel.UserText = value.ToString();
                    result = ValueResult.Success;
                    detail = $"Set text on panel '{panel.NickName}'.";
                    return true;
                case GH_Scribble scribble:
                    scribble.Text = value.ToString();
                    result = ValueResult.Success;
                    detail = $"Set text on scribble '{scribble.NickName}'.";
                    return true;
                case GH_BooleanToggle toggle:
                    if (!TryGetBoolean(value, out var boolean))
                    {
                        result = ValueResult.Failed;
                        detail = $"Value '{value}' is not a boolean.";
                        return false;
                    }

                    toggle.Value = boolean;
                    result = ValueResult.Success;
                    detail = $"Set toggle '{toggle.NickName}' to {boolean}.";
                    return true;
                case GH_NumberSlider slider:
                    if (!TryGetDecimal(value, out var number))
                    {
                        result = ValueResult.Failed;
                        detail = $"Value '{value}' is not numeric.";
                        return false;
                    }

                    var min = slider.Slider.Minimum;
                    var max = slider.Slider.Maximum;
                    number = Math.Max(min, Math.Min(max, number));
                    slider.SetSliderValue(number);
                    result = ValueResult.Success;
                    detail = $"Set slider '{slider.NickName}' to {number} (range {min}..{max}).";
                    return true;
                case GH_ValueList valueList:
                    var index = FindValueListItem(valueList, value);
                    if (index < 0)
                    {
                        result = ValueResult.NotFound;
                        detail = $"No value-list item matches '{value}'.";
                        return false;
                    }

                    for (var i = 0; i < valueList.ListItems.Count; i++)
                    {
                        valueList.ListItems[i].Selected = i == index;
                    }

                    result = ValueResult.Success;
                    detail = $"Selected '{valueList.ListItems[index].Name}' in '{valueList.NickName}'.";
                    return true;
                case IGH_Param param:
                    var applied = SetPersistentParamValue(param, value, out detail);
                    result = applied ? ValueResult.Success : ValueResult.Failed;
                    return applied;
                default:
                    result = ValueResult.Unsupported;
                    detail = $"Object type '{obj.GetType().Name}' does not accept a direct value.";
                    return false;
            }
        }

        private static bool SetPersistentParamValue(IGH_Param param, JToken value, out string detail)
        {
            var persistentBase = FindGenericBaseType(param.GetType(), typeof(GH_PersistentParam<>));
            if (persistentBase == null)
            {
                detail = $"Parameter '{param.NickName}' has no persistent data store.";
                return false;
            }

            var gooType = persistentBase.GetGenericArguments()[0];
            var goo = CreateGoo(gooType, value);
            if (goo == null)
            {
                detail = $"Cannot convert '{value}' to {gooType.Name}.";
                return false;
            }

            var structureType = typeof(GH_Structure<>).MakeGenericType(gooType);
            var structure = Activator.CreateInstance(structureType);
            if (structure == null)
            {
                detail = $"Cannot build persistent data for {gooType.Name}.";
                return false;
            }

            var appendMethod = structureType.GetMethod("Append", new[] { gooType, typeof(GH_Path) });
            if (appendMethod == null)
            {
                detail = $"Cannot append persistent data for {gooType.Name}.";
                return false;
            }

            appendMethod.Invoke(structure, new[] { goo, new GH_Path(0) });
            var setMethod = param.GetType().GetMethod(
                "SetPersistentData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { structureType },
                null);
            if (setMethod == null)
            {
                detail = $"Parameter '{param.NickName}' does not expose SetPersistentData.";
                return false;
            }

            setMethod.Invoke(param, new[] { structure });
            var wired = param.Kind == GH_ParamKind.input && param.SourceCount > 0
                ? " Its input is wired, so the connected value may override this default."
                : string.Empty;
            detail = $"Set persistent {gooType.Name} value of '{param.NickName}'.{wired}";
            return true;
        }

        private static object? CreateGoo(Type gooType, JToken value)
        {
            var candidates = new List<object>();
            switch (value.Type)
            {
                case JTokenType.Boolean:
                    candidates.Add(value.Value<bool>());
                    break;
                case JTokenType.Integer:
                    var integer = value.Value<long>();
                    if (integer >= int.MinValue && integer <= int.MaxValue) candidates.Add((int)integer);
                    candidates.Add(integer);
                    candidates.Add(value.Value<double>());
                    break;
                case JTokenType.Float:
                    candidates.Add(value.Value<double>());
                    candidates.Add(value.Value<decimal>());
                    break;
                case JTokenType.String:
                    candidates.Add(value.Value<string>()!);
                    break;
            }

            foreach (var candidate in candidates)
            {
                var constructor = gooType.GetConstructor(new[] { candidate.GetType() });
                if (constructor != null) return constructor.Invoke(new[] { candidate });
            }

            var stringConstructor = gooType.GetConstructor(new[] { typeof(string) });
            return stringConstructor?.Invoke(new object[] { value.ToString() });
        }

        private static IGH_Param? ResolveInputParam(IGH_Component component, string paramName)
        {
            if (int.TryParse(paramName, out var index) && index >= 0 && index < component.Params.Input.Count)
            {
                return component.Params.Input[index];
            }

            return component.Params.Input.FirstOrDefault(param =>
                string.Equals(param.NickName, paramName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(param.Name, paramName, StringComparison.OrdinalIgnoreCase));
        }

        private static int FindValueListItem(GH_ValueList valueList, JToken value)
        {
            if (value.Type == JTokenType.Integer)
            {
                var index = value.Value<int>();
                return index >= 0 && index < valueList.ListItems.Count ? index : -1;
            }

            var text = value.ToString();
            for (var i = 0; i < valueList.ListItems.Count; i++)
            {
                var item = valueList.ListItems[i];
                if (string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Expression, text, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.Value?.ToString(), text, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool TryGetBoolean(JToken value, out bool result)
        {
            if (value.Type == JTokenType.Boolean)
            {
                result = value.Value<bool>();
                return true;
            }

            return bool.TryParse(value.ToString(), out result);
        }

        private static bool TryGetDecimal(JToken value, out decimal result)
        {
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                result = value.Value<decimal>();
                return true;
            }

            return decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        private static Type? FindGenericBaseType(Type type, Type genericDefinition)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.IsGenericType && current.GetGenericTypeDefinition() == genericDefinition)
                {
                    return current;
                }
            }

            return null;
        }
    }
}
