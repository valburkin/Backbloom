#pragma once

#ifndef PCH_ENABLED
	#include <QObject>
	#include <map>
#endif

#include <utils/Logger.h>
#include <utils/Components.h>

class LightingInstance;

class ComponentController : public QObject
{
	Q_OBJECT

public:
    ComponentController(LightingInstance* lightingInstance, bool disableOnStartup);
    virtual ~ComponentController();

	int isComponentEnabled(overglow::Components comp) const;
	const std::map<overglow::Components, bool>& getComponentsState() const;

signals:
	void SignalComponentStateChanged(overglow::Components comp, bool state);
	void SignalRequestComponent(overglow::Components component, bool enabled);

public slots:
	void setNewComponentState(overglow::Components comp, bool activated);
	void turnGrabbers(bool activated, bool includingSystemGrabber, bool includingVideoGrabber);

private slots:
	void handleCompStateChangeRequest(overglow::Components comps, bool activated);

private:
	LoggerName _log;
	std::map<overglow::Components, bool> _componentStates;
	std::map<overglow::Components, bool> _prevComponentStates;
	std::map<overglow::Components, bool> _prevGrabbers;
	bool _disableOnStartup;
};
