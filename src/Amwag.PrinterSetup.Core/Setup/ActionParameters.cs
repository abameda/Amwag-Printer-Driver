namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>Keys of <see cref="PlannedAction.Parameters"/>.</summary>
internal static class ActionParameters
{
    public const string PackageId = "packageId";
    public const string DriverName = "driverName";
    /// <summary>ChangeQueueDriver: the driver the queue is returned to when the ticket defaults cannot be set with the new one.</summary>
    public const string RestoreDriverName = "restoreDriverName";
    public const string DriverEnvironment = "environment";
    public const string HardwareId = "hardwareId";
    public const string InstanceId = "instanceId";
    public const string QueueName = "queueName";
    public const string PortName = "portName";
    public const string HostAddress = "hostAddress";
    public const string PortNumber = "portNumber";
    public const string Connection = "connection";
    public const string FormName = "formName";
    public const string WidthMicrometers = "widthMicrometers";
    public const string HeightMicrometers = "heightMicrometers";

    public const string UsbConnection = "usb";
    public const string TcpIpConnection = "tcpip";
}
