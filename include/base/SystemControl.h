#pragma once

#ifndef PCH_ENABLED
	#include <QTimer>
#endif

#include <image/Image.h>
#include <utils/Logger.h>
#include <utils/settings.h>
#include <utils/Components.h>

class LightingInstance;

class SystemControl : public QObject
{
	Q_OBJECT

public:
	SystemControl(LightingInstance* lightingInstance);
	~SystemControl();

	quint8 getCapturePriority();

	void setSysCaptureEnable(bool enable);

private slots:
	void handleCompStateChangeRequest(overglow::Components component, bool enable);
	void handleSettingsUpdate(settings::type type, const QJsonDocument& config);
	void handleSysImage(const QString& name, const Image<ColorRgb>& image);
	void setSysInactive();

private:
	LightingInstance* _instance;

	bool	_sysCaptEnabled;
	bool	_alive;
	quint8	_sysCaptPrio;
	QString	_sysCaptName;
	QTimer*	_sysInactiveTimer;
};
