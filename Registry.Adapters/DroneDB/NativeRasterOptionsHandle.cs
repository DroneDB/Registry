using System;
using System.Runtime.InteropServices;
using Registry.Ports.DroneDB;

namespace Registry.Adapters.DroneDB;

/// <summary>
/// The native <c>DDBRasterOptions</c> of a <see cref="RasterAnalysisOptions"/> (or of a NoData value alone), in unmanaged
/// memory for the length of one call: <see cref="Pointer"/> is null when nothing is set (libddb takes it as no options).
/// The struct is the one of ABI 2.4 (64 bytes, with <c>rescaleUnit</c>).
/// Dispose after the call.
/// </summary>
internal sealed unsafe class NativeRasterOptionsHandle : IDisposable
{
    private NativeRasterOptions* _options;
    private IntPtr _formula;
    private IntPtr _rescaleUnit;

    private NativeRasterOptionsHandle()
    {
    }

    /// <summary>The struct to pass, or null for no options.</summary>
    internal NativeRasterOptions* Pointer => _options;

    internal static NativeRasterOptionsHandle Create(RasterAnalysisOptions? options) =>
        Create(options?.Nodata, options?.Formula, options?.Bbox, options?.RescaleUnit);

    internal static NativeRasterOptionsHandle Create(double? nodata) => Create(nodata, null, null, null);

    /// <param name="rescaleUnit">The unit of the rescale of a thermal formula ; null or blank: not set.</param>
    internal static NativeRasterOptionsHandle Create(double? nodata, string? formula, double[]? bbox, string? rescaleUnit)
    {
        var handle = new NativeRasterOptionsHandle();
        if (nodata is null && string.IsNullOrWhiteSpace(formula) && bbox is null && string.IsNullOrWhiteSpace(rescaleUnit))
            return handle;

        if (nodata is { } value && !double.IsFinite(value))
            throw new ArgumentException("The NoData value must be a finite number", nameof(nodata));
        if (bbox is not null && (bbox.Length != 4 || Array.Exists(bbox, v => !double.IsFinite(v))))
            throw new ArgumentException("The window must be four finite numbers: minLon, minLat, maxLon, maxLat",
                nameof(bbox));

        try
        {
            var native = (NativeRasterOptions*)NativeMemory.AllocZeroed((nuint)sizeof(NativeRasterOptions));
            handle._options = native;
            native->StructSize = (uint)sizeof(NativeRasterOptions);
            if (nodata is { } nd)
            {
                native->Flags |= NativeRasterOptions.FlagNodata;
                native->Nodata = nd;
            }

            if (bbox is not null)
            {
                native->Flags |= NativeRasterOptions.FlagBbox;
                for (var i = 0; i < 4; i++)
                    native->Bbox[i] = bbox[i];
            }

            if (!string.IsNullOrWhiteSpace(formula))
            {
                handle._formula = Marshal.StringToCoTaskMemUTF8(formula);
                native->Formula = handle._formula;
            }

            if (!string.IsNullOrWhiteSpace(rescaleUnit))
            {
                handle._rescaleUnit = Marshal.StringToCoTaskMemUTF8(rescaleUnit.Trim());
                native->RescaleUnit = handle._rescaleUnit;
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_formula != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_formula);
            _formula = IntPtr.Zero;
        }

        if (_rescaleUnit != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(_rescaleUnit);
            _rescaleUnit = IntPtr.Zero;
        }

        if (_options != null)
        {
            NativeMemory.Free(_options);
            _options = null;
        }
    }
}
