#pragma once

#ifndef PCH_ENABLED
	#include <QObject>
	#include <QStringList>
#endif

#include <image/ColorRgb.h>
#include <image/Image.h>
#include <utils/Logger.h>
#include <utils/LutLoader.h>

// Base of the system (screen) grabber: capture settings, the HDR tone mapping table and the restart handling
class Grabber : public QObject
{
	Q_OBJECT

public:
	Grabber(const QString& configurationPath, const QString& grabberName);

	static const QString AUTO_SETTING;

	virtual void setHdrToneMappingEnabled(int mode) = 0;

	void setCropping(unsigned cropLeft, unsigned cropRight, unsigned cropTop, unsigned cropBottom);

	bool setWidthHeight(int width, int height);

	bool setFramerate(int fps);

	void enableHardwareAcceleration(bool hardware);

	void setMonitorNits(int nits);

	void setReorderDisplays(int order);

	void setDeviceVideoStandard(const QString& device);

	void setBlocked();

	void unblockAndRestart(bool running);

	QStringList getVideoDevices() const;

public slots:
	virtual bool start() = 0;

	virtual void stop() = 0;

signals:
	void SignalNewCapturedFrame(const Image<ColorRgb>& image);

protected:
	virtual void uninit() = 0;

	// shows a "please wait" image while the default LUT is created in the background
	void pleaseWaitForLut();

	int getTargetSystemFrameDimension(int& targetSizeX, int& targetSizeY);

	int getTargetSystemFrameDimension(int actualWidth, int actualHeight, int& targetSizeX, int& targetSizeY);

	QString		_configurationPath;
	LoggerName	_log;

	int			_hdrToneMappingEnabled;
	LutLoader	_lut;

	/// maximum size of the captured frame (larger frames are scaled down) [pixels]
	int			_width;
	int			_height;
	int			_fps;

	/// number of pixels to crop after capturing
	unsigned	_cropLeft, _cropRight, _cropTop, _cropBottom;

	QString		_deviceName;
	QStringList	_devices;

	bool		_blocked;
	bool		_restartNeeded;
	bool		_initialized;
	bool		_hardware;

	int			_actualWidth, _actualHeight;
	uint		_targetMonitorNits;
	int			_reorderDisplays;

private:
	void restart(const char* reason);
};
