using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Raw;
using static Avalonia.X11.XLib;

namespace Avalonia.X11
{
    internal unsafe class X11DragDrop
    {
        private readonly X11Info _x11;
        private readonly IntPtr _windowHandle;
        private DataObject _currentDrag;
        private int _formatsToProcess;
        private readonly IDragDropDevice _dragDevice;
        private readonly IInputRoot _target;
        private readonly Action<RawInputEventArgs> _dispatch;
        private Point _lastPoint;

        public X11DragDrop(X11Info x11, IntPtr windowHandle, IInputRoot target, Action<RawInputEventArgs> dispatch)
        {
            _x11 = x11;
            _windowHandle = windowHandle;
            _target = target;
            _dispatch = dispatch;
            _dragDevice = AvaloniaLocator.Current.GetService<IDragDropDevice>();
        }

        public void InitializeWindowDnd()
        {
            // Notify the X server that this window supports drag and drop.
            // This shouldn't brake anything even if the window doesn't use DnD events.
            uint dndVersionHint = 4;
            XChangeProperty(_x11.Display, _windowHandle,
                _x11.Atoms.XdndAware, _x11.Atoms.XA_ATOM, 32,
                PropertyMode.Replace, ref dndVersionHint, 1);
        }

        public void HandleDragEnter(XClientMessageEvent ev)
        {
            // I see no need to bother checking if there are more than three data types available
            // and dividing the logic based on that. The extended data type list contains all the 
            // available types anyways.
            XGetWindowProperty(_x11.Display, ev.ptr1, _x11.Atoms.XdndTypeList, IntPtr.Zero,
                new IntPtr(0x8000000), false, (IntPtr)Atom.XA_ATOM, out var actualType,
                out int actualFormat, out var nItems, out var bytesAfter, out var prop);
            if (actualType != (IntPtr)Atom.XA_ATOM || actualFormat != 32 || nItems == IntPtr.Zero || prop == IntPtr.Zero)
            {
                if (prop != IntPtr.Zero)
                    XFree(prop);
                return;
            }

            _currentDrag = new DataObject();
            var type = (IntPtr*)prop;
            _formatsToProcess = (int)nItems;
            for (int i = 0; i < (int)nItems; i++)
            {
                string formatKey = Marshal.PtrToStringAnsi(XGetAtomName(_x11.Display, *type));
                XGetSelectionOwner(_x11.Display, _x11.Atoms.XdndSelection);
                XConvertSelection(_x11.Display, _x11.Atoms.XdndSelection, *type,
                    _x11.Atoms.XdndActionPrivate, _windowHandle, (IntPtr)0);
                type++;
            }
        }

        public void HandleDragPosition(XClientMessageEvent ev)
        {
            int x = (int)ev.ptr3 >> 16;
            int y = (int)ev.ptr3 & 0x0000FFFF;
            var action = ev.ptr5;

            XTranslateCoordinates(_x11.Display, _x11.RootWindow, _windowHandle,
                x, y, out var tx, out var ty, out _);

            var args = new RawDragEvent(
                _dragDevice,
                RawDragEventType.DragOver,
                _target,
                _lastPoint = new Point(tx, ty),
                _currentDrag,
                DragDropEffects.Copy,
                RawInputModifiers.None);
            _dispatch(args);

            var statusEvent = new XEvent
            {
                ClientMessageEvent = new XClientMessageEvent
                {
                    display = _x11.Display,
                    window = ev.ptr1,
                    message_type = _x11.Atoms.XdndStatus,
                    format = 32,
                    ptr1 = _windowHandle,
                    ptr2 = (IntPtr)1,
                    ptr3 = (IntPtr)0,
                    ptr4 = (IntPtr)0,
                    ptr5 = _x11.Atoms.XdndActionCopy
                },
                type = XEventName.ClientMessage
            };
            XSendEvent(_x11.Display, ev.ptr1, false, (IntPtr)EventMask.NoEventMask, ref statusEvent);
            XFlush(_x11.Display);
        }

        public void HandleDragLeave(XClientMessageEvent ev)
        {
            var args = new RawDragEvent(
                _dragDevice,
                RawDragEventType.DragLeave,
                _target,
                default,
                null,
                DragDropEffects.None,
                RawInputModifiers.None);
            _dispatch(args);
            _currentDrag = null;
        }

        public void HandleDragDrop(XClientMessageEvent ev)
        {
            var args = new RawDragEvent(
                _dragDevice,
                RawDragEventType.Drop,
                _target,
                _lastPoint,
                _currentDrag,
                DragDropEffects.Copy,
                RawInputModifiers.None);
            _dispatch(args);
            _currentDrag = null;
        }

        public void HandleSelectionEvent(XSelectionEvent ev)
        {
            // ev.target - MIME type
            if (ev.property == IntPtr.Zero)
                return;

            if (ev.selection == _x11.Atoms.XdndSelection)
            {
                XGetWindowProperty(_x11.Display, _windowHandle, ev.property, IntPtr.Zero, new IntPtr(0x7fffffff), false, (IntPtr)Atom.AnyPropertyType,
                    out var actualType, out var actualFormat, out var nItems, out var bytes_after, out var prop);

                if (nItems != IntPtr.Zero && prop != IntPtr.Zero)
                {
                    var data = new byte[(int)nItems * (actualFormat / 8)];
                    Marshal.Copy(prop, data, 0, data.Length);
                    var encoding = GetEncoding(ev.target, actualType, data, out bool containsBom);
                    if (containsBom)
                        data = data.Skip(2).ToArray();
                    var text = encoding.GetString(data);
                    var targetString = Marshal.PtrToStringAnsi(XGetAtomName(_x11.Display, ev.target));

                    _currentDrag ??= new DataObject();
                    _currentDrag.Set(targetString, text);
                }
                else if (prop != IntPtr.Zero)
                    XFree(prop);

                if (--_formatsToProcess == 0)
                {
                    var args = new RawDragEvent(
                        _dragDevice,
                        RawDragEventType.DragEnter,
                        _target,
                        new Point(0, 0),
                        _currentDrag,
                        DragDropEffects.Move,
                        RawInputModifiers.None);
                    _dispatch(args);
                }
            }
        }

        private Encoding GetEncoding(IntPtr target, IntPtr actualType, byte[] data, out bool containsBom)
        {
            containsBom = false;

            // Check if 
            var encoding = Encoding.UTF8;
            if (actualType == _x11.Atoms.XA_STRING || actualType == _x11.Atoms.OEMTEXT)
                encoding = Encoding.ASCII;
            if (actualType == _x11.Atoms.UTF8_STRING)
                encoding = Encoding.UTF8;
            if (actualType == _x11.Atoms.UTF16_STRING || actualType == _x11.Atoms.UNICODETEXT)
                encoding = Encoding.Unicode;

            // No meaningful match in actualType, check if we have charset in target
            var targetString = Marshal.PtrToStringAnsi(XGetAtomName(_x11.Display, actualType));
            Match match;
            encoding = (match = Regex.Match(targetString, @"(?<=;charset=)(.*?)(?=;|\?|$)")).Success ? Encoding.GetEncoding(match.Value) : encoding;
            // If the encoding is Unicode and the data includes a BOM (which it does in most cases), then handle it appropriately.
            if (encoding is UnicodeEncoding && data.Length >= 2 && data[0] + data[1] == 0xFE + 0xFF)
            {
                encoding = new UnicodeEncoding(data[0] == 0xFE, false);
                containsBom = true;
            }

            return encoding;
        }
    }
}
