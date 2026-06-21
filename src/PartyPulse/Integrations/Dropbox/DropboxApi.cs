using System;
using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace PartyPulse.Integrations.Dropbox;

/// <summary>
/// Dropbox-specific adapter. Keep all endpoint names and reflection-based
/// compatibility code here so settlement orchestration is not coupled to
/// Dropbox internals.
/// </summary>
public sealed class DropboxApi : ExternalPluginIpcClient
{
    private const string DropboxPluginName = "Dropbox";
    private const string DropboxPluginInternalName = "Dropbox";
    private const string ItemQueueUiTypeName = "Dropbox.ItemQueueUI";
    private const string ItemQuantitiesFieldName = "ItemQuantities";

    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> beginTradingQueue;

    public DropboxApi(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log)
        : base(pluginInterface, log, DropboxPluginName, DropboxPluginInternalName)
    {
        isBusy = pluginInterface.GetIpcSubscriber<bool>("Dropbox.IsBusy");
        beginTradingQueue = pluginInterface.GetIpcSubscriber<object>("Dropbox.BeginTradingQueue");
    }

    public PluginIntegrationResult EnsureNotBusy()
    {
        const string operation = "check whether Dropbox is busy";
        var result = ExecutePluginCall(operation, isBusy.InvokeFunc);
        if (!result.Success)
        {
            return PluginIntegrationResult.Failed(result.Failure!);
        }

        return result.Value
            ? Failed(
                PluginIntegrationFailureKind.Busy,
                "PLUGIN_BUSY",
                "Dropbox is already processing another trade.",
                operation)
            : PluginIntegrationResult.Succeeded();
    }

    public PluginIntegrationResult ValidateQueueAccess() =>
        ExecutePluginCall(
            "validate access to the Dropbox item queue",
            () =>
            {
                _ = GetItemQuantities();
            });

    public PluginIntegrationResult ClearQueue() =>
        ExecutePluginCall(
            "clear the Dropbox item queue",
            () => GetItemQuantities().Clear());

    public PluginIntegrationResult TrySetDropboxItemQuantity(
        uint itemId,
        bool hq,
        int quantity)
    {
        const string operation = "set a Dropbox item quantity";
        if (quantity < 0)
        {
            return Failed(
                PluginIntegrationFailureKind.InvalidRequest,
                "INVALID_QUANTITY",
                "The Dropbox item quantity cannot be negative.",
                operation);
        }

        return ExecutePluginCall(
            operation,
            () => SetItemQuantity(itemId, hq, quantity));
    }

    public PluginIntegrationResult BeginTrade() =>
        ExecutePluginCall(
            "begin the Dropbox trade queue",
            beginTradingQueue.InvokeAction);

    private IDictionary GetItemQuantities()
    {
        Type? itemQueueType = null;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var owner = PluginInterface.GetPlugin(assembly);
            if (owner is null ||
                (!string.Equals(
                    owner.InternalName,
                    DropboxPluginInternalName,
                    StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(
                    owner.Name,
                    DropboxPluginName,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            itemQueueType = assembly.GetType(ItemQueueUiTypeName, false, false);
            if (itemQueueType is not null)
            {
                break;
            }
        }

        if (itemQueueType is null)
        {
            throw ContractFailure(
                "DROPBOX_QUEUE_TYPE_NOT_FOUND",
                $"Dropbox no longer exposes the expected type '{ItemQueueUiTypeName}'.");
        }

        var field = itemQueueType.GetField(
            ItemQuantitiesFieldName,
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw ContractFailure(
                "DROPBOX_QUEUE_FIELD_NOT_FOUND",
                $"Dropbox no longer exposes the expected queue field '{ItemQuantitiesFieldName}'.");

        return field.GetValue(null) as IDictionary
            ?? throw ContractFailure(
                "DROPBOX_QUEUE_SHAPE_CHANGED",
                "Dropbox's item quantity queue no longer has the expected dictionary shape.");
    }

    private void SetItemQuantity(uint itemId, bool hq, int quantity)
    {
        var quantities = GetItemQuantities();
        var dictionaryType = quantities.GetType();
        var genericArguments = dictionaryType.GetGenericArguments();
        if (genericArguments.Length != 2)
        {
            throw ContractFailure(
                "DROPBOX_QUEUE_GENERIC_SHAPE_CHANGED",
                "Dropbox's item quantity queue no longer has the expected generic shape.");
        }

        var keyType = genericArguments[0];
        var valueType = genericArguments[1];
        var key = CreateItemDescriptor(keyType, itemId, hq);

        if (quantity == 0)
        {
            if (quantities.Contains(key))
            {
                quantities.Remove(key);
            }

            return;
        }

        object box;
        if (quantities.Contains(key))
        {
            box = quantities[key]
                ?? throw ContractFailure(
                    "DROPBOX_QUEUE_NULL_VALUE",
                    "Dropbox returned an invalid null quantity box for an existing queue item.");
        }
        else
        {
            var valueConstructor = FindConstructor(valueType, typeof(int));
            if (valueConstructor is not null)
            {
                box = valueConstructor.Invoke(new object[] { quantity });
                quantities.Add(key, box);
                return;
            }

            var defaultConstructor = FindConstructor(valueType)
                ?? throw ContractFailure(
                    "DROPBOX_BOX_CONSTRUCTOR_CHANGED",
                    "Dropbox's quantity box no longer has a supported constructor.");
            box = defaultConstructor.Invoke(null);
            quantities.Add(key, box);
        }

        var valueProperty = valueType.GetProperty(
            "Value",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (valueProperty is not null && valueProperty.CanWrite)
        {
            valueProperty.SetValue(box, quantity);
            return;
        }

        var valueField = valueType.GetField(
            "Value",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw ContractFailure(
                "DROPBOX_BOX_VALUE_CHANGED",
                "Dropbox's quantity box no longer exposes a writable Value member.");
        valueField.SetValue(box, quantity);
    }

    private static object CreateItemDescriptor(
        Type keyType,
        uint itemId,
        bool hq)
    {
        var unsignedConstructor = FindConstructor(keyType, typeof(uint), typeof(bool));
        if (unsignedConstructor is not null)
        {
            return unsignedConstructor.Invoke(new object[] { itemId, hq });
        }

        var signedConstructor = FindConstructor(keyType, typeof(int), typeof(bool));
        if (signedConstructor is not null && itemId <= int.MaxValue)
        {
            return signedConstructor.Invoke(new object[] { (int)itemId, hq });
        }

        throw ContractFailure(
            "DROPBOX_ITEM_DESCRIPTOR_CHANGED",
            "Dropbox's ItemDescriptor constructor is incompatible with PartyPulse.");
    }

    private static ConstructorInfo? FindConstructor(Type type, params Type[] argumentTypes) =>
        type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            argumentTypes,
            null);

    private static PluginIntegrationContractException ContractFailure(
        string code,
        string message) =>
        new(code, message);
}
