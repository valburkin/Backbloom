/* Grabber.cpp
*
*  MIT License
*
*  Copyright (c) 2020-2026 awawa-dev
*
*  Project homesite: https://github.com/awawa-dev/HyperHDR
*
*  Permission is hereby granted, free of charge, to any person obtaining a copy
*  of this software and associated documentation files (the "Software"), to deal
*  in the Software without restriction, including without limitation the rights
*  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
*  copies of the Software, and to permit persons to whom the Software is
*  furnished to do so, subject to the following conditions:
*
*  The above copyright notice and this permission notice shall be included in all
*  copies or substantial portions of the Software.

*  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
*  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
*  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
*  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
*  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
*  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
*  SOFTWARE.
 */

#include <base/Grabber.h>
#include <utils/DefaultLut.h>
#include <utils/GlobalSignals.h>
#include <utils/InternalClock.h>

const QString Grabber::AUTO_SETTING = QString("auto");

namespace
{
	const unsigned char pleasewait[] = { 0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xfc,0x3f,0xff,0xff,0xff,0xff,0xff,0xff,0xfd,0xdf,0xff,0xff,0xff,0xff,0xff,0xff,0xfc,0x1f,0xff,0xff,0xff,0x31,0x88,0x25,0xb5,0xfe,0x21,0x9f,0xfe,0xdb,0x7b,0x6d,0xb6,0x7f,0x6d,0xbf,0xfe,0xfb,0xc,0x6d,0xb5,0xbf,0x6d,0xbf,0xfe,0xdb,0x6f,0x6d,0xb5,0xbf,0x6d,0xbf,0xff,0x30,0x98,0xc5,0x6,0x3f,0x6d,0x1f,0xff,0xff,0xff,0xef,0xff,0xff,0x7f,0xbf,0xff,0xff,0xff,0xef,0xff,0xff,0x7f,0xbf,0xff,0xff,0xff,0xfd,0xff,0xfe,0x7f,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xfe,0x3c,0x44,0x13,0x9f,0xdb,0x84,0x4f,0xff,0x7e,0xbd,0xbd,0x7f,0xdb,0xb6,0xdf,0xff,0x7e,0x86,0x3b,0xf,0xa5,0xc6,0xdf,0xff,0xe,0xb7,0xb7,0x6f,0xb6,0xf6,0xdf,0xff,0x76,0xcc,0x79,0x9f,0x24,0x8c,0x8f,0xff,0x76,0xff,0xff,0xff,0xff,0xff,0xdf,0xff,0x76,0xff,0xff,0xff,0xff,0xff,0xdf,0xfe,0xc,0xff,0xff,0xff,0xff,0xfe,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff };
};

Grabber::Grabber(const QString& configurationPath, const QString& grabberName)
	: _configurationPath(configurationPath)
	, _log(grabberName.toUpper())
	, _hdrToneMappingEnabled(0)
	, _width(0)
	, _height(0)
	, _fps(20)
	, _cropLeft(0)
	, _cropRight(0)
	, _cropTop(0)
	, _cropBottom(0)
	, _blocked(false)
	, _restartNeeded(false)
	, _initialized(false)
	, _hardware(false)
	, _actualWidth(0)
	, _actualHeight(0)
	, _targetMonitorNits(200)
	, _reorderDisplays(0)
{
}

void Grabber::pleaseWaitForLut()
{
	Image<ColorRgb> image(_actualWidth, _actualHeight);
	double sizeX = _actualWidth / 64.0;
	double sizeY = _actualHeight / 32.0;

	auto timer = InternalClock::now() / 1200;

	ColorRgb fgColor, bgColor;

	if (timer % 8 < 2)
	{
		bgColor = ColorRgb::WHITE;
		fgColor = ColorRgb::BLACK;
	}
	else if (timer % 8 < 4)
	{
		bgColor = ColorRgb::RED;
		fgColor = ColorRgb::BLACK;
	}
	else if (timer % 8 < 6)
	{
		bgColor = ColorRgb::GREEN;
		fgColor = ColorRgb::BLACK;
	}
	else
	{
		bgColor = ColorRgb::BLUE;
		fgColor = ColorRgb::WHITE;
	}

	image.fastBox(0, 0, image.width(), image.height(), bgColor.red, bgColor.green, bgColor.blue);

	for (int y = 0; y < 32; y++)
		for (int x = 0; x < 64; x++)
		{
			int index = (31 - y) * 8 + (x / 8);
			uint8_t bit = 1 << (7 - (x % 8));
			if (index < static_cast<int>(sizeof(pleasewait)) && !(pleasewait[index] & bit))
			{
				int fromX = sizeX * x;
				int fromY = sizeY * y;
				image.fastBox(fromX, fromY, fromX + sizeX, fromY + sizeY, fgColor.red, fgColor.green, fgColor.blue);
			}
		}

	emit GlobalSignals::getInstance()->SignalNewSystemImage(_deviceName, image);

	DefaultLut::createFileInBackground(_configurationPath);
}

void Grabber::restart(const char* reason)
{
	if (_initialized && !_blocked)
	{
		Debug(_log, "Restarting the grabber due to change of {:s}", reason);
		uninit();
		start();
	}
	else
	{
		Info(_log, "Delayed restart of the grabber due to change of {:s}", reason);
		_restartNeeded = true;
	}
}

void Grabber::setMonitorNits(int nits)
{
	if (static_cast<int>(_targetMonitorNits) != nits)
	{
		_targetMonitorNits = nits;
		Debug(_log, "Set nits to {:d}", _targetMonitorNits);
		restart("the monitor nits value");
	}
}

void Grabber::setReorderDisplays(int order)
{
	if (_reorderDisplays != order)
	{
		_reorderDisplays = order;
		Debug(_log, "Set re-order display permutation to {:d}", _reorderDisplays);
		restart("the display order");
	}
}

void Grabber::setCropping(unsigned cropLeft, unsigned cropRight, unsigned cropTop, unsigned cropBottom)
{
	_cropLeft = cropLeft;
	_cropRight = cropRight;
	_cropTop = cropTop;
	_cropBottom = cropBottom;
}

void Grabber::enableHardwareAcceleration(bool hardware)
{
	if (_hardware != hardware)
	{
		_hardware = hardware;
		Debug(_log, "Set hardware acceleration to {:s}", _hardware ? "enabled" : "disabled");
		restart("the hardware acceleration");
	}
}

void Grabber::unblockAndRestart(bool running)
{
	if (_restartNeeded && running)
	{
		Debug(_log, "Planned restart of the grabber after reloading of the configuration");

		uninit();
		start();
	}

	_blocked = false;
	_restartNeeded = false;
}

void Grabber::setBlocked()
{
	Info(_log, "Restarting the grabber is now blocked due to reloading of the configuration");

	_blocked = true;
	_restartNeeded = false;
}

bool Grabber::setFramerate(int fps)
{
	if (_fps != fps)
	{
		_fps = fps;
		restart("the framerate");
		return true;
	}
	return false;
}

bool Grabber::setWidthHeight(int width, int height)
{
	if ((width >= 0 && height >= 0) && (_width != width || _height != height))
	{
		Debug(_log, "Set new width: {:d}, height: {:d} for capture", width, height);
		_width = width;
		_height = height;
		restart("the capturing resolution");
		return true;
	}
	return false;
}

void Grabber::setDeviceVideoStandard(const QString& device)
{
	if (_deviceName != device)
	{
		Debug(_log, "Selected device changed. Old: '{:s}' new: '{:s}'", (_deviceName), (device));

		const bool firstSelection = _deviceName.isEmpty();
		_deviceName = device;

		if (!firstSelection && !_blocked)
		{
			uninit();
			start();
		}
		else
		{
			Info(_log, "Delayed restart of the grabber due to change of selected device");
			_restartNeeded = true;
		}
	}
}

QStringList Grabber::getVideoDevices() const
{
	return _devices;
}

int Grabber::getTargetSystemFrameDimension(int& targetSizeX, int& targetSizeY)
{
	int startX = _cropLeft;
	int startY = _cropTop;
	int realSizeX = _actualWidth - startX - _cropRight;
	int realSizeY = _actualHeight - startY - _cropBottom;

	if (realSizeX <= 16 || realSizeY <= 16)
	{
		realSizeX = _actualWidth;
		realSizeY = _actualHeight;
	}

	return getTargetSystemFrameDimension(realSizeX, realSizeY, targetSizeX, targetSizeY);
}

int Grabber::getTargetSystemFrameDimension(int actualWidth, int actualHeight, int& targetSizeX, int& targetSizeY)
{
	int checkWidth = actualWidth;
	int divide = 1;

	while (checkWidth > _width)
	{
		divide++;
		checkWidth = actualWidth / divide;
	}

	targetSizeX = actualWidth / divide;
	targetSizeY = actualHeight / divide;

	return divide;
}
