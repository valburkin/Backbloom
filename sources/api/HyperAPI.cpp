#ifndef PCH_ENABLED	
	#include <QResource>
	#include <QBuffer>
	#include <QByteArray>
	#include <QTimer>
	#include <QList>
	#include <QHostAddress>
	#include <QMultiMap>

	#include <chrono>
	#include <csignal>
#endif

#include <QCoreApplication>
#include <QHostInfo>

#include <OverglowConfig.h>
#include <api/HyperAPI.h>
#include <led-drivers/LedDeviceWrapper.h>
#include <led-drivers/LedDeviceManufactory.h>
#include <led-drivers/net/ProviderRestApi.h>

#include <base/SystemWrapper.h>
#include <base/SoundCapture.h>
#include <base/ImageToLedManager.h>
#include <base/AccessManager.h>
#include <json-utils/jsonschema/QJsonUtils.h>
#include <json-utils/jsonschema/QJsonSchemaChecker.h>
#include <json-utils/JsonUtils.h>
#include <performance-counters/PerformanceCounters.h>

// bonjour wrapper
#include <bonjour/DiscoveryWrapper.h>

using namespace overglow;

// Strictly validates the global "layout" setting (LayoutSpec envelope). Unlike the rest of the
// configuration, an invalid layout is rejected instead of being silently auto-corrected.
static bool validateLayoutSetting(const QJsonValue& layout, QString& error)
{
	if (!layout.isObject())
	{
		error = "layout must be an object";
		return false;
	}

	Q_INIT_RESOURCE(resource);

	QJsonObject layoutSchema;
	try
	{
		layoutSchema = QJsonUtils::readSchema(":/schema-layout.json");
	}
	catch (const std::runtime_error& ex)
	{
		error = QString("could not load layout schema: %1").arg(ex.what());
		return false;
	}

	QJsonSchemaChecker checker;
	checker.setSchema(layoutSchema);
	if (!checker.validate(layout.toObject()).first)
	{
		error = checker.getMessages().join("; ");
		return false;
	}
	return true;
}

HyperAPI::HyperAPI(QString peerAddress, const LoggerName& log, bool localConnection, QObject* parent, bool noListener)
	: CallbackAPI(log, localConnection, parent)
{
	_noListener = noListener;
	_peerAddress = peerAddress;
	_streaming_logging_activated = false;
	_ledStreamTimer = new QTimer(this);
	_colorsStreamingInterval = 50;
	_lastSentImage = 0;

	connect(_ledStreamTimer, &QTimer::timeout, this, &HyperAPI::handleLedColorsTimer, Qt::UniqueConnection);

	Q_INIT_RESOURCE(JSONRPC_schemas);
}

void HyperAPI::handleMessage(const QString& messageString, const QString& httpAuthHeader)
{
	try
	{
		if (LightingInstance::isTerminated())
			return;

		const QString ident = "JsonRpc@" + _peerAddress;
		QJsonObject message;
		// parse the message
		if (!JsonUtils::parse(ident, messageString, message, _log))
		{
			sendErrorReply("Errors during message parsing, please consult the Overglow Log.");
			return;
		}

		int tan = 0;
		if (message.value("tan") != QJsonValue::Undefined)
			tan = message["tan"].toInt();

		// check basic message
		if (!JsonUtils::validate(ident, message, ":schema", _log))
		{
			sendErrorReply("Errors during message validation, please consult the Overglow Log.", "" /*command*/, tan);
			return;
		}

		// check specific message
		const QString command = message["command"].toString();
		if (!JsonUtils::validate(ident, message, QString(":schema-%1").arg(command), _log))
		{
			sendErrorReply("Errors during specific message validation, please consult the Overglow Log", command, tan);
			return;
		}

		// client auth before everything else but not for http
		if (!_noListener && command == "authorize")
		{
			handleAuthorizeCommand(message, command, tan);
			return;
		}

		// check auth state
		if (!BaseAPI::isAuthorized())
		{
			bool authOk = false;

			if (_noListener)
			{
				QString cToken = httpAuthHeader.mid(5).trimmed();
				if (BaseAPI::isTokenAuthorized(cToken))
					authOk = true;
			}

			if (!authOk)
			{
				sendErrorReply("No Authorization", command, tan);
				return;
			}
		}

		bool isRunning = false;
		quint8 currentIndex = getCurrentInstanceIndex();

		SAFE_CALL_1_RET(_instanceManager.get(), IsInstanceRunning, bool, isRunning, quint8, currentIndex);
		if (_instance == nullptr || !isRunning)
		{
			sendErrorReply("Not ready", command, tan);
			return;
		}
		else
		{
			// switch over all possible commands and handle them
			if (command == "color")
				handleColorCommand(message, command, tan);
			else if (command == "image")
				handleImageCommand(message, command, tan);
			else if (command == "effect")
				handleEffectCommand(message, command, tan);
			else if (command == "sysinfo")
				handleSysInfoCommand(message, command, tan);
			else if (command == "serverinfo")
				handleServerInfoCommand(message, command, tan);
			else if (command == "clear")
				handleClearCommand(message, command, tan);
			else if (command == "adjustment")
				handleAdjustmentCommand(message, command, tan);
			else if (command == "sourceselect")
				handleSourceSelectCommand(message, command, tan);
			else if (command == "config")
				handleConfigCommand(message, command, tan);
			else if (command == "componentstate")
				handleComponentStateCommand(message, command, tan);
			else if (command == "ledcolors")
				handleLedColorsCommand(message, command, tan);
			else if (command == "logging")
				handleLoggingCommand(message, command, tan);
			else if (command == "processing")
				handleProcessingCommand(message, command, tan);
			else if (command == "instance")
				handleInstanceCommand(message, command, tan);
			else if (command == "leddevice")
				handleLedDeviceCommand(message, command, tan);
			else if (command == "save-db")
				handleSaveDB(message, command, tan);
			else if (command == "load-db")
				handleLoadDB(message, command, tan);
			else if (command == "tunnel")
				handleTunnel(message, command, tan);
			else if (command == "performance-counters")
				handlePerformanceCounters(message, command, tan);
			else if (command == "clearall")
				handleClearallCommand(message, command, tan);
			else if (command == "help")
				handleHelpCommand(message, command, tan);
			else if (command == "smoothing")
				handleSmoothingCommand(message, command, tan);
			else if (command == "current-state")
				handleCurrentStateCommand(message, command, tan);
			// handle not implemented commands
			else
				handleNotImplemented(command, tan);
		}
	}
	catch (...)
	{
		sendErrorReply("Exception");
	}
}

void HyperAPI::initialize()
{
	// init API, REQUIRED!
	BaseAPI::init();

	// setup auth interface
	connect(this, &BaseAPI::SignalPendingTokenClientNotification, this, &HyperAPI::newPendingTokenRequest);
	connect(this, &BaseAPI::SignalTokenClientNotification, this, &HyperAPI::handleTokenResponse);

	// listen for killed instances
	connect(_instanceManager.get(), &InstanceManager::SignalInstanceStateChanged, this, &HyperAPI::handleInstanceStateChange);

	// pipe callbacks from subscriptions to parent
	connect(this, &CallbackAPI::SignalCallbackToClient, this, &HyperAPI::SignalCallbackJsonMessage);
}

bool HyperAPI::handleInstanceSwitch(quint8 inst, bool /*forced*/)
{
	if (BaseAPI::switchToInstance(inst))
	{		
		Debug(_log, "Client '{:s}' switch to Overglow instance {:d}", (_peerAddress), inst);
		return true;
	}
	return false;
}

void HyperAPI::handleColorCommand(const QJsonObject& message, const QString& command, int tan)
{
	int priority = message["priority"].toInt();
	int duration = message["duration"].toInt(-1);
	const QString origin = message["origin"].toString("JsonRpc") + "@" + _peerAddress;

	const QJsonArray& jsonColor = message["color"].toArray();
	std::vector<uint8_t> colors;
	// TODO faster copy
	for (auto&& entry : jsonColor)
	{
		colors.emplace_back(uint8_t(entry.toInt()));
	}

	BaseAPI::setColor(priority, colors, duration, origin);
	sendSuccessReply(command, tan);
}

void HyperAPI::handleImageCommand(const QJsonObject& message, const QString& command, int tan)
{
	BaseAPI::ImageCmdData idata;
	idata.priority = message["priority"].toInt();
	idata.origin = message["origin"].toString("JsonRpc") + "@" + _peerAddress;
	idata.duration = message["duration"].toInt(-1);
	idata.width = message["imagewidth"].toInt();
	idata.height = message["imageheight"].toInt();
	idata.scale = message["scale"].toInt(-1);
	idata.format = message["format"].toString();
	idata.imgName = message["name"].toString("");
	idata.imagedata = message["imagedata"].toString();
	QString replyMsg;

	if (!BaseAPI::setImage(idata, COMP_IMAGE, replyMsg))
	{
		sendErrorReply(replyMsg, command, tan);
		return;
	}
	sendSuccessReply(command, tan);
}

void HyperAPI::handleEffectCommand(const QJsonObject& message, const QString& command, int tan)
{
	EffectCmdData dat;
	dat.priority = message["priority"].toInt();
	dat.duration = message["duration"].toInt(-1);
	dat.pythonScript = message["pythonScript"].toString();
	dat.origin = message["origin"].toString("JsonRpc") + "@" + _peerAddress;
	dat.effectName = message["effect"].toObject()["name"].toString();
	dat.data = message["imageData"].toString("").toUtf8();
	dat.args = message["effect"].toObject()["args"].toObject();

	if (BaseAPI::setEffect(dat))
		sendSuccessReply(command, tan);
	else
		sendErrorReply("Effect '" + dat.effectName + "' not found", command, tan);
}

void HyperAPI::handleServerInfoCommand(const QJsonObject& message, const QString& command, int tan)
{
	try
	{
		bool subscribeOnly = false;
		if (message.contains("subscribe"))
		{
			QJsonArray subsArr = message["subscribe"].toArray();
			for (const QJsonValueRef entry : subsArr)
			{
				if (entry == "performance-update")
					subscribeOnly = true;
			}
		}

		if (!subscribeOnly)
		{
			QJsonObject info;


			/////////////////////
			// Instance report //
			/////////////////////

			SAFE_CALL_1_RET(_instance.get(), getJsonInfo, QJsonObject, info, bool, true);

			///////////////////////////
			// Available LED devices //
			///////////////////////////

			QJsonObject ledDevices;
			QJsonArray availableLedDevices;
			for (const auto& dev : overglow::leds::GET_ALL_LED_DEVICE(nullptr))
			{
				QJsonObject driver;
				driver["name"] = dev.name;
				driver["group"] = dev.group;
				availableLedDevices.append(driver);
			}

			ledDevices["available"] = availableLedDevices;
			info["ledDevices"] = ledDevices;

			///////////////////////
			// Sound Device Info //
			///////////////////////

			QJsonObject resultSound;

			if (_soundCapture != nullptr)
				SAFE_CALL_0_RET(_soundCapture.get(), getJsonInfo, QJsonObject, resultSound);

			if (!resultSound.isEmpty())
				info["sound"] = resultSound;

			/////////////////////////
			// System Grabber Info //
			/////////////////////////

			QJsonObject resultSGrabber;

			if (_systemGrabber != nullptr && _systemGrabber->systemWrapper() != nullptr)
				SAFE_CALL_0_RET(_systemGrabber->systemWrapper(), getJsonInfo, QJsonObject, resultSGrabber);

			if (!resultSGrabber.isEmpty())
				info["systemGrabbers"] = resultSGrabber;

			/////////////////////////////////
			//  Devices found on the network  //
			/////////////////////////////////

			QJsonArray sessions;

			QList<DiscoveryRecord> services;
			if (_discoveryWrapper != nullptr)
				SAFE_CALL_0_RET(_discoveryWrapper.get(), getAllServices, QList<DiscoveryRecord>, services);

			for (const auto& session : services)
			{
				QJsonObject item;
				item["name"] = session.getName();
				item["host"] = session.hostName;
				item["address"] = session.address;
				item["port"] = session.port;
				sessions.append(item);
			}
			info["sessions"] = sessions;

			///////////////////////////
			//     Instances info    //
			///////////////////////////

			QJsonArray instanceInfo;
			for (const auto& entry : BaseAPI::getAllInstanceData())
			{
				QJsonObject obj;
				obj.insert("friendly_name", entry["friendly_name"].toString());
				obj.insert("instance", entry["instance"].toInt());
				obj.insert("running", entry["running"].toBool());
				instanceInfo.append(obj);
			}
			info["instance"] = instanceInfo;
			info["currentInstance"] = getCurrentInstanceIndex();


			/////////////////
			//     MISC    //
			/////////////////

			info["hostname"] = QHostInfo::localHostName();
			info["lastError"] = Logger::getInstance()->getLastError();


			////////////////
			//     END    //
			////////////////

			sendSuccessDataReply(QJsonDocument(info), command, tan);
		}
		else
			sendSuccessReply(command, tan);

		// AFTER we send the info, the client might want to subscribe to future updates
		if (message.contains("subscribe"))
		{
			// check if listeners are allowed
			if (_noListener)
				return;

			CallbackAPI::subscribe(message["subscribe"].toArray());
		}
	}
	catch (...)
	{
		sendErrorReply("Exception");
	}
}

void HyperAPI::handleClearCommand(const QJsonObject& message, const QString& command, int tan)
{
	int priority = message["priority"].toInt();
	QString replyMsg;

	if (!BaseAPI::clearPriority(priority, replyMsg))
	{
		sendErrorReply(replyMsg, command, tan);
		return;
	}
	sendSuccessReply(command, tan);
}

void HyperAPI::handleClearallCommand(const QJsonObject& /*message*/, const QString& command, int tan)
{
	QString replyMsg;
	BaseAPI::clearPriority(-1, replyMsg);
	sendSuccessReply(command, tan);
}

void HyperAPI::handleHelpCommand(const QJsonObject& /*message*/, const QString& command, int tan)
{
	QJsonObject req;

	req["available_commands"] = "color, image, effect, serverinfo, clear, clearall, adjustment, sourceselect, config, componentstate, ledcolors, logging, processing, sysinfo, authorize, instance, leddevice, smoothing, current-state, performance-counters, save-db, load-db, tunnel, help";
	sendSuccessDataReply(QJsonDocument(req), command, tan);
}

void HyperAPI::handleCurrentStateCommand(const QJsonObject& message, const QString& command, int tan)
{
	const QString& subc = message["subcommand"].toString().trimmed();
	int instance = message["instance"].toInt(0);

	if (subc == "average-color")
	{
		QJsonObject avColor = BaseAPI::getAverageColor(instance);
		sendSuccessDataReply(QJsonDocument(avColor), command + "-" + subc, tan);
	}
	else
		handleNotImplemented(command, tan);
}

void HyperAPI::handleSmoothingCommand(const QJsonObject& message, const QString& command, int tan)
{
	const QString& subc = message["subcommand"].toString().trimmed().toLower();
	int time = message["time"].toInt();

	if (subc=="all")
		QUEUE_CALL_1(_instanceManager.get(), setSmoothing, int, time)
	else
		QUEUE_CALL_1(_instance.get(), setSmoothing, int, time);

	sendSuccessReply(command, tan);
}

void HyperAPI::handleSourceSelectCommand(const QJsonObject& message, const QString& command, int tan)
{
	if (message.contains("auto"))
	{
		BaseAPI::setSourceAutoSelect(message["auto"].toBool(false));
	}
	else if (message.contains("priority"))
	{
		BaseAPI::setVisiblePriority(message["priority"].toInt());
	}
	else
	{
		sendErrorReply("Priority request is invalid", command, tan);
		return;
	}
	sendSuccessReply(command, tan);
}

void HyperAPI::handleSaveDB(const QJsonObject& /*message*/, const QString& command, int tan)
{
	if (_adminAuthorized)
	{
		QJsonObject backup;

		SAFE_CALL_0_RET(_instanceManager.get(), getBackup, QJsonObject, backup);

		if (!backup.empty())
			sendSuccessDataReply(QJsonDocument(backup), command, tan);
		else
			sendErrorReply("Error while generating the backup file, please consult the Overglow logs.", command, tan);
	}
	else
		sendErrorReply("No Authorization", command, tan);
}

void HyperAPI::handleLoadDB(const QJsonObject& message, const QString& command, int tan)
{
	if (_adminAuthorized)
	{
		QString error;

		SAFE_CALL_1_RET(_instanceManager.get(), restoreBackup, QString, error, QJsonObject, message);

		if (error.isEmpty())
		{
			LightingInstance::signalTerminateTriggered();
			QTimer::singleShot(0, _instanceManager.get(), []() {QCoreApplication::quit(); });
		}
		else
			sendErrorReply("Error occured while restoring the backup: " + error, command, tan);
	}
	else
		sendErrorReply("No Authorization", command, tan);
}

void HyperAPI::handlePerformanceCounters(const QJsonObject& message, const QString& command, int tan)
{
	QString subcommand = message["subcommand"].toString("");

	if (subcommand == "all")
	{
		QUEUE_CALL_1(_performanceCounters.get(), performanceInfoRequest, bool, true);
		sendSuccessReply(command, tan);
	}
	else if (subcommand == "resources")
	{
		QUEUE_CALL_1(_performanceCounters.get(), performanceInfoRequest, bool, false);
		sendSuccessReply(command, tan);
	}
	else
		sendErrorReply("Unknown subcommand", command, tan);
}

void HyperAPI::handleConfigCommand(const QJsonObject& message, const QString& command, int tan)
{
	QString subcommand = message["subcommand"].toString("");
	QString full_command = command + "-" + subcommand;

	if (subcommand == "getschema")
	{
		handleSchemaGetCommand(message, full_command, tan);
	}
	else if (subcommand == "setconfig")
	{
		if (_adminAuthorized)
			handleConfigSetCommand(message, full_command, tan);
		else
			sendErrorReply("No Authorization", command, tan);
	}
	else if (subcommand == "getconfig")
	{
		if (_adminAuthorized)
		{
			QJsonObject getconfig;
			SAFE_CALL_0_RET(_instance.get(), getJsonConfig, QJsonObject, getconfig);
			sendSuccessDataReply(QJsonDocument(getconfig), full_command, tan);
		}
		else
			sendErrorReply("No Authorization", command, tan);
	}
	else
	{
		sendErrorReply("unknown or missing subcommand", full_command, tan);
	}
}

void HyperAPI::handleConfigSetCommand(const QJsonObject& message, const QString& command, int tan)
{
	if (message.contains("config"))
	{
		QJsonObject config = message["config"].toObject();
		if (BaseAPI::isInstanceEnabled())
		{
			const QString layoutKey = settings::typeToString(settings::type::LAYOUT);
			QString layoutError;
			if (config.contains(layoutKey) && !validateLayoutSetting(config[layoutKey], layoutError))
			{
				sendErrorReply("Invalid layout: " + layoutError, command, tan);
				return;
			}

			// Merge a partial config onto the current one, so that a client sending only some
			// top-level sections (e.g. just "layout") does not reset the others to defaults.
			QJsonObject merged;
			SAFE_CALL_0_RET(_instance.get(), getJsonConfig, QJsonObject, merged);
			for (auto it = config.constBegin(); it != config.constEnd(); ++it)
				merged[it.key()] = it.value();

			if (BaseAPI::saveSettings(merged))
			{
				sendSuccessReply(command, tan);
			}
			else
			{
				sendErrorReply("Save settings failed", command, tan);
			}
		}
		else
			sendErrorReply("Saving configuration while Overglow is disabled isn't possible", command, tan);
	}
}

void HyperAPI::handleSchemaGetCommand(const QJsonObject& /*message*/, const QString& command, int tan)
{
	// create result
	QJsonObject schemaJson, alldevices, properties;

	// make sure the resources are loaded (they may be left out after static linking)
	Q_INIT_RESOURCE(resource);

	// read the settings json schema from the resource
	QString schemaFile = ":/settings-schema";

	try
	{
		schemaJson = QJsonUtils::readSchema(schemaFile);
	}
	catch (const std::runtime_error& error)
	{
		throw std::runtime_error(error.what());
	}

	// collect all LED Devices
	properties = schemaJson["properties"].toObject();
	alldevices = LedDeviceWrapper::getLedDeviceSchemas();
	properties.insert("alldevices", alldevices);

	// collect all available effect schemas
	schemaJson.insert("properties", properties);

	// send the result
	sendSuccessDataReply(QJsonDocument(schemaJson), command, tan);
}

void HyperAPI::handleComponentStateCommand(const QJsonObject& message, const QString& command, int tan)
{
	const QJsonObject& componentState = message["componentstate"].toObject();
	QString comp = componentState["component"].toString("invalid");
	bool compState = componentState["state"].toBool(true);
	QString replyMsg;

	if (!BaseAPI::setComponentState(comp, compState, replyMsg))
	{
		sendErrorReply(replyMsg, command, tan);
		return;
	}
	sendSuccessReply(command, tan);
}

void HyperAPI::handleIncomingColors(const QVector<ColorRgb>& ledValues)
{
	_currentLedValues = ledValues;

	if (_ledStreamTimer->interval() != _colorsStreamingInterval)
		_ledStreamTimer->start(_colorsStreamingInterval);
}

void HyperAPI::handleLedColorsTimer()
{
	streamLedcolorsUpdate(_currentLedValues);
}

void HyperAPI::handleLedColorsCommand(const QJsonObject& message, const QString& command, int tan)
{
	// create result
	QString subcommand = message["subcommand"].toString("");

	// max 20 Hz (50ms) interval for streaming (default: 10 Hz (100ms))
	_colorsStreamingInterval = qMax(message["interval"].toInt(100), 50);

	if (subcommand == "ledstream-start")
	{
		_streaming_leds_reply["success"] = true;
		_streaming_leds_reply["command"] = command + "-ledstream-update";
		_streaming_leds_reply["tan"] = tan;

		subscribeFor("leds-colors");

		if (!_ledStreamTimer->isActive() || _ledStreamTimer->interval() != _colorsStreamingInterval)
			_ledStreamTimer->start(_colorsStreamingInterval);

		QUEUE_CALL_0(_instance.get(), update);
	}
	else if (subcommand == "ledstream-stop")
	{
		subscribeFor("leds-colors", true);
		_ledStreamTimer->stop();
	}
	else if (subcommand == "imagestream-start")
	{
		if (BaseAPI::isAdminAuthorized())
		{
			_streaming_image_reply["success"] = true;
			_streaming_image_reply["command"] = command + "-imagestream-update";
			_streaming_image_reply["tan"] = tan;

			subscribeFor("live-video");
		}
		else
			sendErrorReply("No Authorization", command, tan);
	}
	else if (subcommand == "imagestream-stop")
	{
		subscribeFor("live-video", true);
	}
	else
	{
		return;
	}

	sendSuccessReply(command + "-" + subcommand, tan);
}

void HyperAPI::handleLoggingCommand(const QJsonObject& message, const QString& command, int tan)
{
	// create result
	QString subcommand = message["subcommand"].toString("");

	if (BaseAPI::isAdminAuthorized())
	{
		_streaming_logging_reply["success"] = true;
		_streaming_logging_reply["command"] = command;
		_streaming_logging_reply["tan"] = tan;

		if (subcommand == "start")
		{
			if (!_streaming_logging_activated)
			{
				_streaming_logging_reply["command"] = command + "-update";
				connect(Logger::getInstance(), &Logger::SignalNewLogMessage, this, &HyperAPI::incommingLogMessage, Qt::UniqueConnection);
				Debug(_log, "log streaming activated for client {:s}", _peerAddress.toStdString().c_str()); // needed to trigger log sending
			}
		}
		else if (subcommand == "stop")
		{
			if (_streaming_logging_activated)
			{
				disconnect(Logger::getInstance(), &Logger::SignalNewLogMessage, this, &HyperAPI::incommingLogMessage);
				_streaming_logging_activated = false;
				Debug(_log, "log streaming deactivated for client  {:s}", _peerAddress.toStdString().c_str());
			}
		}
		else
		{
			return;
		}

		sendSuccessReply(command + "-" + subcommand, tan);
	}
	else
	{
		sendErrorReply("No Authorization", command + "-" + subcommand, tan);
	}
}

void HyperAPI::handleProcessingCommand(const QJsonObject& message, const QString& command, int tan)
{
	BaseAPI::setLedMappingType(ImageToLedManager::mappingTypeToInt(message["mappingType"].toString("multicolor_mean")));
	sendSuccessReply(command, tan);
}

void HyperAPI::handleInstanceCommand(const QJsonObject& message, const QString& command, int tan)
{
	const QString& subc = message["subcommand"].toString();
	const quint8& inst = message["instance"].toInt();
	const QString& name = message["name"].toString();

	if (subc == "switchTo")
	{
		if (handleInstanceSwitch(inst))
		{
			QJsonObject obj;
			obj["instance"] = inst;
			sendSuccessDataReply(QJsonDocument(obj), command + "-" + subc, tan);
		}
		else
			sendErrorReply("Selected Overglow instance isn't running", command + "-" + subc, tan);
		return;
	}

	if (subc == "startInstance")
	{
		connect(this, &BaseAPI::SignalInstanceStartedClientNotification,
				this, [this, command, subc](const int& tan) { sendSuccessReply(command + "-" + subc, tan); });
		if (!BaseAPI::startInstance(inst, tan))
			sendErrorReply("Can't start Overglow instance index " + QString::number(inst), command + "-" + subc, tan);
		return;
	}

	if (subc == "stopInstance")
	{
		// silent fail
		BaseAPI::stopInstance(inst);
		sendSuccessReply(command + "-" + subc, tan);
		return;
	}

	if (subc == "deleteInstance")
	{
		QString replyMsg;
		if (BaseAPI::deleteInstance(inst, replyMsg))
			sendSuccessReply(command + "-" + subc, tan);
		else
			sendErrorReply(replyMsg, command + "-" + subc, tan);
		return;
	}

	// create and save name requires name
	if (name.isEmpty())
		sendErrorReply("Name string required for this command", command + "-" + subc, tan);

	if (subc == "createInstance")
	{
		QString replyMsg = BaseAPI::createInstance(name);
		if (replyMsg.isEmpty())
			sendSuccessReply(command + "-" + subc, tan);
		else
			sendErrorReply(replyMsg, command + "-" + subc, tan);
		return;
	}

	if (subc == "saveName")
	{
		QString replyMsg = BaseAPI::setInstanceName(inst, name);
		if (replyMsg.isEmpty())
			sendSuccessReply(command + "-" + subc, tan);
		else
			sendErrorReply(replyMsg, command + "-" + subc, tan);
		return;
	}
}

void HyperAPI::handleLedDeviceCommand(const QJsonObject& message, const QString& command, int tan)
{
	Debug(_log, "message: [{:s}]", QString(QJsonDocument(message).toJson(QJsonDocument::Compact)).toUtf8().constData());

	const QString& subc = message["subcommand"].toString().trimmed();
	const QString& devType = message["ledDeviceType"].toString().trimmed();

	QString full_command = command + "-" + subc;

	// TODO: Validate that device type is a valid one
/*	if ( ! valid type )
	{
		sendErrorReply("Unknown device", full_command, tan);
	}
	else
*/ {
		QJsonObject config;
		config.insert("type", devType);
		std::unique_ptr<LedDevice> ledDevice;

		if (subc == "discover")
		{
			ledDevice = std::unique_ptr<LedDevice>(overglow::leds::CONSTRUCT_LED_DEVICE(config));
			const QJsonObject& params = message["params"].toObject();
			const QJsonObject devicesDiscovered = ledDevice->discover(params);

			Debug(_log, "response: [{:s}]", QString(QJsonDocument(devicesDiscovered).toJson(QJsonDocument::Compact)).toUtf8().constData());

			sendSuccessDataReply(QJsonDocument(devicesDiscovered), full_command, tan);
		}
		else if (subc == "getProperties")
		{
			ledDevice = std::unique_ptr<LedDevice>(overglow::leds::CONSTRUCT_LED_DEVICE(config));
			const QJsonObject& params = message["params"].toObject();
			const QJsonObject deviceProperties = ledDevice->getProperties(params);

			Debug(_log, "response: [{:s}]", QString(QJsonDocument(deviceProperties).toJson(QJsonDocument::Compact)).toUtf8().constData());

			sendSuccessDataReply(QJsonDocument(deviceProperties), full_command, tan);
		}
		else if (subc == "identify")
		{
			QJsonObject params = message["params"].toObject();

			if (devType == "philipshuev2" || devType == "blink")
			{
				QUEUE_CALL_1(_instance.get(), identifyLed, QJsonObject, params);
			}
			else
			{
				ledDevice = std::unique_ptr<LedDevice>(overglow::leds::CONSTRUCT_LED_DEVICE(config));
				ledDevice->identify(params);
			}

			sendSuccessReply(full_command, tan);
		}
		else if (subc == "hasLedClock")
		{
			int hasLedClock = 0;
			QJsonObject ret;
			SAFE_CALL_0_RET(_instance.get(), hasLedClock, int, hasLedClock);
			ret["hasLedClock"] = hasLedClock;
			sendSuccessDataReply(QJsonDocument(ret), "hasLedClock-update", tan);
		}
		else
		{
			sendErrorReply("Unknown or missing subcommand", full_command, tan);
		}		
	}
}

void HyperAPI::streamLedcolorsUpdate(const QVector<ColorRgb>& ledColors)
{
	QJsonObject result;
	QJsonArray leds;

	for (const auto& color : ledColors)
	{
		leds << QJsonValue(color.red) << QJsonValue(color.green) << QJsonValue(color.blue);
	}

	result["leds"] = leds;
	_streaming_leds_reply["result"] = result;

	// send the result
	emit SignalCallbackJsonMessage(_streaming_leds_reply);
}

void HyperAPI::handlerInstanceImageUpdated(const Image<ColorRgb>& image)
{
	_liveImage = image;
	QUEUE_CALL_0(this, sendImage);
}

void HyperAPI::sendImage()
{
	qint64 _currentTime = InternalClock::now();
	int timeLimit = (_liveImage.width() > 1280) ? 60 : 30;

	if ((_liveImage.width() <= 1 || _liveImage.height() <= 1) ||
		(_lastSentImage + timeLimit > _currentTime && _lastSentImage + 100 < _currentTime))
	{
		return;
	}

	emit SignalCallbackBinaryImageMessage(_liveImage);

	_liveImage = Image<ColorRgb>();
	_lastSentImage = _currentTime;
}

void HyperAPI::incommingLogMessage(const Logger::T_LOG_MESSAGE& msg)
{
	QJsonObject result, message;
	QJsonArray messageArray;

	if (!_streaming_logging_activated)
	{
		_streaming_logging_activated = true;
		SAFE_CALL_0_RET(Logger::getInstance(), getLogMessageBuffer, QJsonArray, messageArray);
	}
	else
	{
		message["loggerName"] = msg.loggerName;
		message["function"] = msg.function;
		message["line"] = QString::number(msg.line);
		message["fileName"] = msg.fileName;
		message["message"] = msg.message;
		message["levelString"] = msg.levelString;
		message["utime"] = QString::number(msg.utime);

		messageArray.append(message);
	}

	result.insert("messages", messageArray);
	_streaming_logging_reply["result"] = result;

	// send the result
	emit SignalCallbackJsonMessage(_streaming_logging_reply);
}

void HyperAPI::newPendingTokenRequest(const QString& id, const QString& comment)
{
	QJsonObject obj;
	obj["comment"] = comment;
	obj["id"] = id;
	obj["timeout"] = 180000;

	sendSuccessDataReply(QJsonDocument(obj), "authorize-tokenRequest", 1);
}

void HyperAPI::handleTokenResponse(bool success, const QString& token, const QString& comment, const QString& id, const int& tan)
{
	const QString cmd = "authorize-requestToken";
	QJsonObject result;
	result["token"] = token;
	result["comment"] = comment;
	result["id"] = id;

	if (success)
		sendSuccessDataReply(QJsonDocument(result), cmd, tan);
	else
		sendErrorReply("Token request timeout or denied", cmd, tan);
}

void HyperAPI::handleInstanceStateChange(InstanceState state, quint8 instance, const QString& /*name*/)
{
	switch (state)
	{
	case InstanceState::STOP:
		if (getCurrentInstanceIndex() == instance)
		{
			handleInstanceSwitch();
		}
		break;
	default:
		break;
	}
}

void HyperAPI::stopDataConnections()
{
	disconnect(Logger::getInstance(), nullptr, this, nullptr);
	_streaming_logging_activated = false;
	CallbackAPI::removeSubscriptions();
	// led stream colors
	disconnect(_instance.get(), &LightingInstance::SignalRawColorsChanged, this, nullptr);
	_ledStreamTimer->stop();
}

void HyperAPI::handleTunnel(const QJsonObject& message, const QString& command, int tan)
{
	const QString& subcommand = message["subcommand"].toString().trimmed();
	const QString& full_command = command + "-" + subcommand;

	if (_adminAuthorized)
	{
		const QString& ip = message["ip"].toString().trimmed();
		const QString& path = message["path"].toString().trimmed();
		const QString& data = message["data"].toString().trimmed();
		const QString& service = message["service"].toString().trimmed();

		if (service == "hue" || service == "home_assistant")
		{
			QUrl tempUrl(ip.startsWith("http") ? ip : "http://" + ip);
			if (!path.startsWith("/clip/v2") && !path.startsWith("/api"))
			{
				sendErrorReply("Invalid path", full_command, tan);
				return;
			}

			ProviderRestApi provider;

			QUrl url = QUrl(QString("%1://").arg(tempUrl.scheme()) + tempUrl.host() + ((service == "home_assistant" && tempUrl.port() >= 0) ? ":" + QString::number(tempUrl.port()) : "") + path);

			Debug(_log, "Tunnel request for: {:s}", (url.toString()));

			if (!url.isValid())
			{
				sendErrorReply("Invalid Philips Hue bridge address", full_command, tan);
				return;
			}

			if (!isLocal(url.host()))
			{
				Error(_log, "Could not resolve '{:s}' as IP local address at your Overglow host device.", (url.host()));
				sendErrorReply("The Philips Hue wizard supports only valid IP addresses in the LOCAL network.\nIt may be preferable to use the IP4 address instead of the host name if you are having problems with DNS resolution.", full_command, tan);
				return;
			}
			httpResponse result;
			const QJsonObject& headerObject = message["header"].toObject();

			if (!headerObject.isEmpty())
			{
				for (const auto& item : headerObject.keys())
				{
					provider.addHeader(item, headerObject[item].toString());
				}
			}

			if (subcommand != "get")
				provider.addHeader("Content-Type", "application/json");

			if (subcommand == "put")
				result = provider.put(url, data);
			else if (subcommand == "post")
				result = provider.post(url, data);
			else if (subcommand == "get")
				result = provider.get(url);
			else
			{
				sendErrorReply("Unknown command", full_command, tan);
				return;
			}

			QJsonObject reply;
			auto doc = result.getBody();
			reply["success"] = true;
			reply["command"] = full_command;
			reply["tan"] = tan;
			reply["isTunnelOk"] = !result.error();
			if (doc.isArray())
				reply["info"] = doc.array();
			else
				reply["info"] = doc.object();

			emit SignalCallbackJsonMessage(reply);
		}
		else
			sendErrorReply("Service not supported", full_command, tan);
	}
	else
		sendErrorReply("No Authorization", full_command, tan);
}

bool HyperAPI::isLocal(QString hostname)
{
	QHostAddress address(hostname);

	if (QAbstractSocket::IPv4Protocol != address.protocol() &&
		QAbstractSocket::IPv6Protocol != address.protocol())
	{
		auto result = QHostInfo::fromName(hostname);
		if (result.error() == QHostInfo::HostInfoError::NoError)
		{
			QList<QHostAddress> list = result.addresses();
			for (int x = 1; x >= 0; x--)
				foreach(const QHostAddress& l, list)
				if (l.protocol() == (x > 0) ? QAbstractSocket::IPv4Protocol : QAbstractSocket::IPv6Protocol)
				{
					address = l;
					x = 0;
					break;
				}
		}
	}

	if (QAbstractSocket::IPv4Protocol == address.protocol())
	{
		quint32 adr = address.toIPv4Address();
		uint8_t a = adr >> 24;
		uint8_t b = (adr >> 16) & 0xff;

		Debug(_log, "IP4 prefix: {:d}.{:d}", a, b);

		return ((a == 192 && b == 168) || (a == 10) || (a == 172 && b >= 16 && b <32));
	}

	else if (QAbstractSocket::IPv6Protocol == address.protocol())
	{
		auto i = address.toString();

		Debug(_log, "IP6 prefix: {:s}", (i.left(4)));

		return i.indexOf("fd") == 0 || i.indexOf("fe80") == 0;
	}

	return false;
}

void HyperAPI::handleSysInfoCommand(const QJsonObject&, const QString& command, int tan)
{
	// create result
	QJsonObject result;
	QJsonObject info;
	result["success"] = true;
	result["command"] = command;
	result["tan"] = tan;

	QJsonObject system;
	putSystemInfo(system);
	info["system"] = system;

	QJsonObject product;
	product["version"] = QString(OVERGLOW_VERSION);
	product["build"] = QString(OVERGLOW_BUILD_ID);
	product["supportUrl"] = QString(OVERGLOW_SUPPORT_URL);
	product["time"] = QString(__DATE__ " " __TIME__);
	product["id"] = _accessManager->getID();

	bool readOnly = true;
	SAFE_CALL_0_RET(_instance.get(), getReadOnlyMode, bool, readOnly);
	product["readOnlyMode"] = readOnly;

	info["overglow"] = product;

	// send the result
	result["info"] = info;
	emit SignalCallbackJsonMessage(result);
}

void HyperAPI::handleAdjustmentCommand(const QJsonObject& message, const QString& command, int tan)
{
	QJsonObject adjustment = message["adjustment"].toObject();

	QUEUE_CALL_1(_instance.get(), updateAdjustments, QJsonObject, adjustment);

	sendSuccessReply(command, tan);
}

void HyperAPI::handleAuthorizeCommand(const QJsonObject& message, const QString& command, int tan)
{
	const QString& subc = message["subcommand"].toString().trimmed();
	const QString& id = message["id"].toString().trimmed();
	const QString& password = message["password"].toString().trimmed();
	const QString& newPassword = message["newPassword"].toString().trimmed();
	const QString& comment = message["comment"].toString().trimmed();

	// catch test if auth is required
	if (subc == "tokenRequired")
	{
		QJsonObject req;
		req["required"] = !BaseAPI::isAuthorized();

		sendSuccessDataReply(QJsonDocument(req), command + "-" + subc, tan);
		return;
	}

	// catch test if admin auth is required
	if (subc == "adminRequired")
	{
		QJsonObject req;
		req["adminRequired"] = !BaseAPI::isAdminAuthorized();
		sendSuccessDataReply(QJsonDocument(req), command + "-" + subc, tan);
		return;
	}

	// the default password is a security risk, replace it asap
	if (subc == "newPasswordRequired")
	{
		QJsonObject req;
		req["newPasswordRequired"] = BaseAPI::hasDefaultPassword();
		sendSuccessDataReply(QJsonDocument(req), command + "-" + subc, tan);
		return;
	}

	// catch logout
	if (subc == "logout")
	{
		BaseAPI::logout();
		sendSuccessReply(command + "-" + subc, tan);
		return;
	}

	// change password
	if (subc == "newPassword")
	{
		// use password, newPassword
		if (BaseAPI::isAdminAuthorized())
		{
			if (BaseAPI::updatePassword(password, newPassword))
			{
				sendSuccessReply(command + "-" + subc, tan);
				return;
			}
			sendErrorReply("Failed to update user password", command + "-" + subc, tan);
			return;
		}
		sendErrorReply("No Authorization", command + "-" + subc, tan);
		return;
	}

	// token created from ui
	if (subc == "createToken")
	{
		// use comment
		// for user authorized sessions
		AccessManager::AuthDefinition def;
		const QString res = BaseAPI::createToken(comment, def);
		if (res.isEmpty())
		{
			QJsonObject newTok;
			newTok["comment"] = def.comment;
			newTok["id"] = def.id;
			newTok["token"] = def.token;

			sendSuccessDataReply(QJsonDocument(newTok), command + "-" + subc, tan);
			return;
		}
		sendErrorReply(res, command + "-" + subc, tan);
		return;
	}

	// rename Token
	if (subc == "renameToken")
	{
		// use id/comment
		const QString res = BaseAPI::renameToken(id, comment);
		if (res.isEmpty())
		{
			sendSuccessReply(command + "-" + subc, tan);
			return;
		}
		sendErrorReply(res, command + "-" + subc, tan);
		return;
	}

	// delete token
	if (subc == "deleteToken")
	{
		// use id
		const QString res = BaseAPI::deleteToken(id);
		if (res.isEmpty())
		{
			sendSuccessReply(command + "-" + subc, tan);
			return;
		}
		sendErrorReply(res, command + "-" + subc, tan);
		return;
	}

	// catch token request
	if (subc == "requestToken")
	{
		// use id/comment
		const bool& acc = message["accept"].toBool(true);
		if (acc)
			BaseAPI::setNewTokenRequest(comment, id, tan);
		else
			BaseAPI::cancelNewTokenRequest(comment, id);
		// client should wait for answer
		return;
	}

	// get pending token requests
	if (subc == "getPendingTokenRequests")
	{
		QVector<AccessManager::AuthDefinition> vec;
		if (BaseAPI::getPendingTokenRequests(vec))
		{
			QJsonArray arr;
			for (const auto& entry : vec)
			{
				QJsonObject obj;
				obj["comment"] = entry.comment;
				obj["id"] = entry.id;
				obj["timeout"] = int(entry.timeoutTime);
				arr.append(obj);
			}
			sendSuccessDataReply(QJsonDocument(arr), command + "-" + subc, tan);
		}
		else
		{
			sendErrorReply("No Authorization", command + "-" + subc, tan);
		}

		return;
	}

	// accept/deny token request
	if (subc == "answerRequest")
	{
		// use id
		const bool& accept = message["accept"].toBool(false);
		if (!BaseAPI::handlePendingTokenRequest(id, accept))
			sendErrorReply("No Authorization", command + "-" + subc, tan);
		return;
	}

	// get token list
	if (subc == "getTokenList")
	{
		QVector<AccessManager::AuthDefinition> defVect;
		if (BaseAPI::getTokenList(defVect))
		{
			QJsonArray tArr;
			for (const auto& entry : defVect)
			{
				QJsonObject subO;
				subO["comment"] = entry.comment;
				subO["id"] = entry.id;
				subO["last_use"] = entry.lastUse;

				tArr.append(subO);
			}
			sendSuccessDataReply(QJsonDocument(tArr), command + "-" + subc, tan);
			return;
		}
		sendErrorReply("No Authorization", command + "-" + subc, tan);
		return;
	}

	// login
	if (subc == "login")
	{
		const QString& token = message["token"].toString().trimmed();

		// catch token
		if (!token.isEmpty())
		{
			// userToken is longer
			if (token.length() > 36)
			{
				if (BaseAPI::isUserTokenAuthorized(token))
					sendSuccessReply(command + "-" + subc, tan);
				else
					sendErrorReply("No Authorization", command + "-" + subc, tan);

				return;
			}
			// usual app token is 36
			if (token.length() == 36)
			{
				if (BaseAPI::isTokenAuthorized(token))
				{
					sendSuccessReply(command + "-" + subc, tan);
				}
				else
					sendErrorReply("No Authorization", command + "-" + subc, tan);
			}
			return;
		}

		// password
		// use password
		if (password.length() >= 8)
		{
			QString userTokenRep;
			if (BaseAPI::isUserAuthorized(password) && BaseAPI::getUserToken(userTokenRep))
			{
				// Return the current valid Overglow user token
				QJsonObject obj;
				obj["token"] = userTokenRep;
				sendSuccessDataReply(QJsonDocument(obj), command + "-" + subc, tan);
			}
			else if (BaseAPI::isUserBlocked())
				sendErrorReply("Too many login attempts detected. Please restart Overglow.", command + "-" + subc, tan);
			else
				sendErrorReply("No Authorization", command + "-" + subc, tan);
		}
		else
			sendErrorReply("Password too short", command + "-" + subc, tan);
	}
}

void HyperAPI::handleNotImplemented(const QString& command, int tan)
{
	sendErrorReply("Command not implemented", command, tan);
}

void HyperAPI::sendSuccessReply(const QString& command, int tan)
{
	// create reply
	QJsonObject reply;
	reply["success"] = true;
	reply["command"] = command;
	reply["tan"] = tan;

	// send reply
	emit SignalCallbackJsonMessage(reply);
}

void HyperAPI::sendSuccessDataReply(const QJsonDocument& doc, const QString& command, int tan)
{
	QJsonObject reply;
	reply["success"] = true;
	reply["command"] = command;
	reply["tan"] = tan;
	if (doc.isArray())
		reply["info"] = doc.array();
	else
		reply["info"] = doc.object();

	emit SignalCallbackJsonMessage(reply);
}

void HyperAPI::sendErrorReply(const QString& error, const QString& command, int tan)
{
	// create reply
	QJsonObject reply;
	reply["success"] = false;
	reply["error"] = error;
	reply["command"] = command;
	reply["tan"] = tan;

	// send reply
	emit SignalCallbackJsonMessage(reply);
}
