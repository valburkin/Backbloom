/* LightingInstance.cpp
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

#ifndef PCH_ENABLED
	#include <QString>
	#include <QStringList>
	#include <QThread>
	#include <QPair>

	#include <exception>
	#include <sstream>	
#endif
#include <QMetaMethod>

#include <OverglowConfig.h>
#include <base/LightingInstance.h>
#include <base/ImageToLedManager.h>
#include <utils/GlobalSignals.h>
#include <led-drivers/LedDeviceWrapper.h>
#include <infinite-color-engine/InfiniteSmoothing.h>
#include <effects/EffectEngine.h>
#include <base/InstanceConfig.h>
#include <base/VideoControl.h>
#include <base/SystemControl.h>
#include <base/GrabberWrapper.h>
#include <base/ComponentController.h>
#include <base/Muxer.h>
#include <base/SoundCapture.h>
#include <base/SystemWrapper.h>
#include <base/GrabberHelper.h>
#include <utils/Logger.h>
#include <infinite-color-engine/InfiniteProcessing.h>
#include <infinite-color-engine/CoreInfiniteEngine.h>
#include <infinite-color-engine/ColorSpace.h>

std::atomic<bool> LightingInstance::_signalTerminate(false);
std::atomic<int>  LightingInstance::_totalRunningCount(0);

LightingInstance::LightingInstance(quint8 instance, bool disableOnStartup, QString name)
	: QObject()
	, _instIndex(instance)
	, _bootEffect(QTime::currentTime().addSecs(5))
	, _ledString()
	, _instanceConfig(nullptr)
	, _componentController(nullptr)
	, _imageProcessor(nullptr)
	, _muxer(nullptr)
	, _ledDeviceWrapper(nullptr)
	, _infinite(nullptr)
	, _effectEngine(nullptr)
	, _videoControl(nullptr)
	, _systemControl(nullptr)
	, _log("")
	, _hwLedCount()
	, _ledGridSize()
	, _currentLedColors()
	, _name((name.isEmpty()) ? QString("INSTANCE%1").arg(instance) : name)
	, _disableOnStartup(disableOnStartup)
{
	_totalRunningCount++;
}

LightingInstance::~LightingInstance()
{
	Info(_log, "Stopping and releasing components of a Overglow instance...");

	disconnect(GlobalSignals::getInstance(), nullptr, this, nullptr);
	disconnect(this, nullptr, nullptr, nullptr);

	if (_muxer != nullptr)
		clear(-1, true);

	Info(_log, "[ 1/8] Releasing Overglow{:d}->VideoControl...", _instIndex);
	_videoControl = nullptr;
	Info(_log, "[ 2/8] Releasing Overglow{:d}->SystemControl...", _instIndex);
	_systemControl = nullptr;
	Info(_log, "[ 3/8] Releasing Overglow{:d}->EffectEngine...", _instIndex);
	_effectEngine = nullptr;
	Info(_log, "[ 4/8] Releasing Overglow{:d}->InstanceConfiguration...", _instIndex);
	_instanceConfig = nullptr;
	Info(_log, "[ 5/8] Releasing Overglow{:d}->LED driver wrapper...", _instIndex);
	_ledDeviceWrapper = nullptr;
	Info(_log, "[ 6/8] Releasing Overglow{:d}->InfiniteColorEngine...", _instIndex);
	_infinite = nullptr;
	Info(_log, "[ 7/8] Releasing Overglow{:d}->ComponentController...", _instIndex);
	_componentController = nullptr;
	Info(_log, "[ 8/8] Releasing Overglow{:d}->Muxer...", _instIndex);
	_muxer = nullptr;

	_totalRunningCount--;

	Warning(_log, "The instance has been released succesfully [left: {:d}]", (int)_totalRunningCount);
}

void LightingInstance::start()
{
	_log = QString("INSTANCE%1").arg(_instIndex);

	if (_signalTerminate)
	{
		Warning(_log, "Signal terminate detected. Skipping start of the instance");
		return;
	}

	Info(_log, "Starting the instance");

	_instanceConfig = std::make_unique<InstanceConfig>(false, _instIndex, this);
	_componentController = std::make_unique<ComponentController>(this, _disableOnStartup);
	connect(_componentController.get(), &ComponentController::SignalComponentStateChanged, this, &LightingInstance::SignalComponentStateChanged);
	_ledString = LedString::createLedString(getSetting(settings::type::LEDS).array(), LedString::createColorOrder(getSetting(settings::type::DEVICE).object()));
	_muxer = std::make_unique<Muxer>(_instIndex, static_cast<int>(_ledString.leds().size()), this);
	_imageProcessor = std::make_unique<ImageToLedManager>(_ledString, this);
	connect(_muxer.get(), &Muxer::SignalPrioritiesChanged, this, &LightingInstance::SignalPrioritiesChanged);
	connect(_muxer.get(), &Muxer::SignalVisiblePriorityChanged, this, &LightingInstance::SignalVisiblePriorityChanged);
	connect(_muxer.get(), &Muxer::SignalVisibleComponentChanged, this, &LightingInstance::SignalVisibleComponentChanged);
	_infinite = std::make_unique<CoreInfiniteEngine>(this);
	_ledGridSize = LedString::getLedLayoutGridSize(getSetting(settings::type::LEDS).array());
	_currentLedColors = QVector<ColorRgb>(_ledString.leds().size(), ColorRgb::BLACK);
	

	connect(_instanceConfig.get(), &InstanceConfig::SignalInstanceSettingsChanged, this, &LightingInstance::SignalInstanceSettingsChanged);

	// handle hwLedCount
	_hwLedCount = qMax(getSetting(settings::type::DEVICE).object()["hardwareLedCount"].toInt(1), getLedCount());

	connect(this, &LightingInstance::SignalVisiblePriorityChanged, this, &LightingInstance::update);
	connect(this, &LightingInstance::SignalVisiblePriorityChanged, this, &LightingInstance::handlePriorityChangedLedDevice);
	connect(this, &LightingInstance::SignalVisibleComponentChanged, this, &LightingInstance::handleVisibleComponentChanged);

	// listen for settings updates of this instance (LEDS & COLOR)
	connect(_instanceConfig.get(), &InstanceConfig::SignalInstanceSettingsChanged, this, &LightingInstance::handleSettingsUpdate);

	// initialize LED-devices
	QJsonObject ledDevice = getSetting(settings::type::DEVICE).object();
	ledDevice["currentLedCount"] = _hwLedCount; // Inject led count info
	

	_ledDeviceWrapper = std::make_unique<LedDeviceWrapper>(this);
	connect(this, &LightingInstance::SignalRequestComponent, _ledDeviceWrapper.get(), &LedDeviceWrapper::handleComponentState);
	_ledDeviceWrapper->createLedDevice(ledDevice, _infinite->getSuggestedInterval(), _infinite->getAntiFlickeringFilterState(), _disableOnStartup);

	// create the effect engine; needs to be initialized after smoothing!
	_effectEngine = std::make_unique<EffectEngine>(this);
	connect(this, &LightingInstance::SignalVisiblePriorityChanged, _effectEngine.get(), &EffectEngine::visiblePriorityChanged);

	// create the Daemon capture interface
	_videoControl = std::make_unique<VideoControl>(this);

	_systemControl = std::make_unique<SystemControl>(this);

	// if there is no startup / background effect and no sending capture interface we probably want to push once BLACK (as PrioMuxer won't emit a priority change)
	update();

	// handle background effect
	QUEUE_CALL_2(this, handleSettingsUpdate,
		settings::type, settings::type::BGEFFECT,
		QJsonDocument, getSetting(settings::type::BGEFFECT));

	emit GlobalSignals::getInstance()->SignalRequestComponent(overglow::Components::COMP_HDR, _instIndex, true);

	// instance initiated, enter thread event loop
	emit SignalInstanceJustStarted();

	if (_disableOnStartup)
	{
		_componentController->setNewComponentState(overglow::COMP_ALL, false);
		Warning(_log, "The user has disabled LEDs auto-start in the configuration (interface: 'General' tab)");
	}

	// exit
	Info(_log, "The instance is running");
}


void LightingInstance::handleSettingsUpdate(settings::type type, const QJsonDocument& config)
{

	//	std::cout << config.toJson().toStdString() << std::endl;

	if (type == settings::type::LEDS)
	{
		const QJsonArray leds = config.array();

		// ledstring, img processor, muxer, ledGridSize (effect-engine image based effects), _ledBuffer and ByteOrder of ledstring
		_ledString = LedString::createLedString(leds, LedString::createColorOrder(getSetting(settings::type::DEVICE).object()));
		_imageProcessor->setLedString(_ledString);
		_ledGridSize = LedString::getLedLayoutGridSize(leds);

		QVector<ColorRgb> color(_ledString.leds().size(), ColorRgb{ 0,0,0 });
		_currentLedColors = color;

		// handle hwLedCount update
		_hwLedCount = qMax(getSetting(settings::type::DEVICE).object()["hardwareLedCount"].toInt(1), getLedCount());
	}
	else if (type == settings::type::DEVICE)
	{
		QJsonObject dev = config.object();

		// handle hwLedCount update
		_hwLedCount = qMax(dev["hardwareLedCount"].toInt(1), getLedCount());

		// do always reinit until the led devices can handle dynamic changes
		dev["currentLedCount"] = _hwLedCount; // Inject led count info
		_ledDeviceWrapper->createLedDevice(dev, _infinite->getSuggestedInterval(), _infinite->getAntiFlickeringFilterState(), false);
	}
	else if (type == settings::type::BGEFFECT || type == settings::type::FGEFFECT)
	{
		bool isBootEffect = (type == settings::type::FGEFFECT);
		int effectPriority = (isBootEffect) ? 0 : 254;

		if (isBootEffect)
		{
			if (QTime::currentTime() < _bootEffect)
				_bootEffect = QTime::currentTime();
			else
				return;
		}
		else
			clear(effectPriority);

		const QJsonObject& effectObject = config.object();

		if (effectObject["enable"].toBool(true))
		{
			const char* effectDesc = (isBootEffect) ? "Boot effect" : "Background effect";
			const QString effectType = effectObject["type"].toString("effect");
			const QString effectAnimation = effectObject["effect"].toString("Rainbow swirl fast");
			const QJsonValue effectColors = effectObject["color"];
			int effectDuration = (isBootEffect) ? effectObject["duration_ms"].toInt(3000) : -1;

			if (isBootEffect && effectDuration <= 0)
			{
				effectDuration = 3000;
				Warning(_log, "Boot effect duration 'infinity' is forbidden, set to default value {:d} ms", effectDuration);
			}

			if (effectType.contains("color"))
			{
				const QJsonArray& COLORS = effectColors.toArray();

				if (COLORS.size() < 3)
					return;

				QVector<ColorRgb> colors = {
					ColorRgb { (uint8_t)(COLORS[0].toInt(0)), (uint8_t)(COLORS[1].toInt(0)), (uint8_t)(COLORS[2].toInt(0))}
				};

				setColor(effectPriority, colors, effectDuration);
				Info(_log, "{:s}: color set to [{:d}, {:d}, {:d}]", effectDesc, colors[0].red, colors[0].green, colors[0].blue);
			}
			else
			{
				int result = setEffect(effectAnimation, effectPriority, effectDuration);
				Info(_log, "{:s}: animation set to '{:s}' [{:s}]", effectDesc, (effectAnimation), ((result == 0) ? "started" : "failed"));
			}
		}
	}

	if (type == settings::type::DEVICE || type == settings::type::LEDS)
		update();
}

void LightingInstance::signalTerminateTriggered()
{
	_signalTerminate = true;
	LedDevice::signalTerminateTriggered();
}

bool LightingInstance::isTerminated()
{
	return _signalTerminate;
}

int LightingInstance::getTotalRunningCount()
{
	return _totalRunningCount;
}

QJsonDocument LightingInstance::getSetting(settings::type type) const
{
	return _instanceConfig->getSetting(type);
}

void LightingInstance::setSetting(settings::type type, QString config)
{
	return _instanceConfig->saveSetting(type, config);
}

bool LightingInstance::saveSettings(const QJsonObject& config, bool correct)
{
	return _instanceConfig->saveSettings(config, correct);
}

void LightingInstance::saveCalibration(QString saveData)
{
	_instanceConfig->saveSetting(settings::type::VIDEODETECTION, saveData);
}

void LightingInstance::setSmoothing(int time)
{
	QJsonDocument tempDoc = getSetting(settings::type::SMOOTHING);
	if (tempDoc.isObject())
	{
		QJsonObject tempSettings = tempDoc.object();
		tempSettings["time_ms"] = QJsonValue(time);
		tempDoc.setObject(tempSettings);
		emit SignalInstanceSettingsChanged(settings::type::SMOOTHING, tempDoc);
	}
}

QJsonObject LightingInstance::getAverageColor()
{
	QJsonObject ret;

	long red = 0, green = 0, blue = 0, count = 0;

	for (const ColorRgb& c : _currentLedColors)
	{
		red += c.red;
		green += c.green;
		blue += c.blue;

		count++;
	}

	if (!_ledDeviceWrapper->enabled())
	{
		red = green = blue = 0;
	}

	if (count > 0)
	{
		ret["red"] = static_cast<int>(red / count);
		ret["green"] = static_cast<int>(green / count);
		ret["blue"] = static_cast<int>(blue / count);
	}

	return ret;
}

unsigned LightingInstance::addEffectConfig(unsigned id, int settlingTime_ms, double ledUpdateFrequency_hz, bool pause)
{
	return _infinite->addCustomSmoothingConfig(id, settlingTime_ms, ledUpdateFrequency_hz, pause);
}

int LightingInstance::getLedCount() const
{
	return static_cast<int>(_ledString.leds().size());
}

bool LightingInstance::getReadOnlyMode() const
{
	return _instanceConfig->isReadOnlyMode();
}

void LightingInstance::setSourceAutoSelect(bool state)
{
	_muxer->setSourceAutoSelectEnabled(state);
}

bool LightingInstance::setVisiblePriority(int priority)
{
	return _muxer->setPriority(priority);
}

bool LightingInstance::sourceAutoSelectEnabled() const
{
	return _muxer->isSourceAutoSelectEnabled();
}

void LightingInstance::setNewComponentState(overglow::Components component, bool state)
{
	if (_componentController == nullptr || _muxer == nullptr)
		return;

	_componentController->setNewComponentState(component, state);

	if (overglow::Components::COMP_LEDDEVICE == component && state)
	{
		QUEUE_CALL_2(this, handleSettingsUpdate,
			settings::type, settings::type::FGEFFECT,
			QJsonDocument, getSetting(settings::type::FGEFFECT));
	}

	if (overglow::Components::COMP_LEDDEVICE == component)
	{
		emit GlobalSignals::getInstance()->SignalPerformanceStateChanged(state, overglow::PerformanceReportType::LED, getInstanceIndex());

		emit SignalInstancePauseChanged(_instIndex, state);
	}
}

int LightingInstance::isComponentEnabled(overglow::Components comp) const
{
	return _componentController->isComponentEnabled(comp);
}

void LightingInstance::registerInput(int priority, overglow::Components component, const QString& origin, const QString& owner, unsigned smooth_cfg)
{
	_muxer->registerInput(priority, component, origin, ColorRgb::BLACK, smooth_cfg, owner);
}

bool LightingInstance::setInputLeds(int priority, const QVector<ColorRgb>& ledColors, int timeout_ms, bool clearEffect)
{
	if (_muxer->setInput(priority, timeout_ms))
	{
		// clear effect if this call does not come from an effect
		if (clearEffect)
		{
			_effectEngine->channelCleared(priority);
		}

		// if this priority is visible, update immediately
		if (priority == _muxer->getCurrentPriority())
		{
			if (_currentLedColors.size() == ledColors.size())
				_currentLedColors = ledColors;
			else
				Warning(_log, "Discrepancy between the number of colors in the input ({:d}) and the current ones ({:d})",
					ledColors.size(), _currentLedColors.size());

			update();
		}

		return true;
	}
	return false;
}

void LightingInstance::setInputImage(int priority, const Image<ColorRgb>& image, int64_t timeout_ms, bool clearEffect)
{
	if (!_muxer->hasPriority(priority))
	{
		return;
	}

	bool isImage = image.width() > 1 && image.height() > 1;

	if (_muxer->setInput(priority, timeout_ms))
	{
		if (clearEffect)
		{
			_effectEngine->channelCleared(priority);
		}

		if (priority == _muxer->getCurrentPriority())
		{
			if (isImage)
			{
				std::vector<linalg::aliases::float3> colors;

				_imageProcessor->processFrame(colors, image);

				if (!colors.empty())
				{
					updateResult(std::move(colors));
					emit SignalInstanceImageUpdated(image);
				}
			}
			else
				update();
		}
	}
}

bool LightingInstance::setInputInactive(quint8 priority)
{
	return _muxer->setInputInactive(priority);
}

void LightingInstance::setColor(int priority, const QVector<ColorRgb>& ledColors, int timeout_ms, const QString& origin, bool clearEffects)
{
	if (ledColors.empty())
		return;

	// clear effect if this call does not come from an effect
	if (clearEffects)
	{
		_effectEngine->channelCleared(priority);
	}

	if (getComponentForPriority(priority) != overglow::COMP_COLOR)
	{
		clear(priority);
	}
	if (getCurrentPriority() == priority)
	{
		emit SignalColorIsSet(ledColors[0], timeout_ms);
	}
	// register color
	_muxer->registerInput(priority, overglow::COMP_COLOR, origin, ledColors[0]);
	_muxer->setInput(priority, timeout_ms);
	update();
}

void LightingInstance::updateAdjustments(const QJsonObject& config)
{
	auto oldConfig = _infinite->getCurrentProcessingConfig();

	auto newSettings = (oldConfig.size() == 1) ? oldConfig.first().toObject() : QJsonObject();

	if (newSettings.isEmpty())
		return;

	for (auto it = config.constBegin(); it != config.constEnd(); ++it)
	{
		newSettings[it.key()] = it.value();
	}

	// pack it
	QJsonArray newConfig;
	newConfig.append(newSettings);
	QJsonObject newObject;	
	newObject["channelAdjustment"] = newConfig;
	QJsonDocument newDoc(newObject);

	// send it
	emit SignalInstanceSettingsChanged(settings::type::COLOR, newDoc);
	emit SignalAdjustmentUpdated(newConfig);
	update();
}

bool LightingInstance::clear(int priority, bool forceClearAll)
{
	bool isCleared = false;
	if (priority < 0)
	{
		_muxer->clearAll(forceClearAll);

		// send clearall signal to the effect engine
		_effectEngine->allChannelsCleared();
		isCleared = true;
	}
	else
	{
		// send clear signal to the effect engine
		// (outside the check so the effect gets cleared even when the effect is not sending colors)
		_effectEngine->channelCleared(priority);

		if (_muxer->clearInput(priority))
		{
			isCleared = true;
		}
	}
	return isCleared;
}

int LightingInstance::getCurrentPriority() const
{
	return _muxer->getCurrentPriority();
}

bool LightingInstance::hasPriority(int priority)
{
	return _muxer->hasPriority(priority);
}

overglow::Components LightingInstance::getComponentForPriority(int priority)
{
	return _muxer->getInputInfo(priority).componentId;
}

overglow::Components LightingInstance::getCurrentPriorityActiveComponent()
{
	return _muxer->getInputInfo(getCurrentPriority()).componentId;
}

std::list<EffectDefinition> LightingInstance::getEffects() const
{
	return _effectEngine->getEffects();
}

QJsonObject LightingInstance::getJsonConfig() const
{
	return _instanceConfig->getSettings();
}

int LightingInstance::setEffect(const QString& effectName, int priority, int timeout, const QString& origin)
{
	if (_effectEngine != nullptr)
		return _effectEngine->runEffect(effectName, priority, timeout, origin);
	else
		return 0;
}

void LightingInstance::setLedMappingType(int mappingType)
{
	_imageProcessor->setLedMappingType(mappingType);
}

void LightingInstance::handleVisibleComponentChanged(overglow::Components comp)
{
	_imageProcessor->setBlackbarDetectDisable((comp == overglow::COMP_EFFECT));
	_infinite->setProcessingEnabled(comp != overglow::COMP_COLOR);
}

void LightingInstance::handlePriorityChangedLedDevice(const quint8& priority)
{
	if (_signalTerminate)
		return;

	int previousPriority = _muxer->getPreviousPriority();

	Info(_log, "New priority[{:d}], previous [{:d}]", priority, previousPriority);
	if (priority == Muxer::LOWEST_PRIORITY)
	{
		Warning(_log, "No source left -> switch LED-Device off");

		emit SignalRequestComponent(overglow::COMP_LEDDEVICE, false);
		emit GlobalSignals::getInstance()->SignalPerformanceStateChanged(false, overglow::PerformanceReportType::INSTANCE, getInstanceIndex());
	}
	else
	{
		if (previousPriority == Muxer::LOWEST_PRIORITY)
		{
			Info(_log, "New source available -> switch LED-Device on");
			if (!isComponentEnabled(overglow::Components::COMP_ALL))
			{
				Warning(_log, "Components are disabled: ignoring switching LED-Device on");
			}
			else
			{
				emit SignalRequestComponent(overglow::COMP_LEDDEVICE, true);
				emit GlobalSignals::getInstance()->SignalPerformanceStateChanged(true, overglow::PerformanceReportType::INSTANCE, getInstanceIndex(), _name);
			}
		}
	}
}

void LightingInstance::update()
{
	if (_signalTerminate)
		return;

	const Muxer::InputInfo& priorityInfo = _muxer->getInputInfo(_muxer->getCurrentPriority());
	if (priorityInfo.componentId == overglow::Components::COMP_COLOR)
	{
		std::fill(_currentLedColors.begin(), _currentLedColors.end(), priorityInfo.staticColor);
	}

	std::vector<linalg::aliases::float3> linear255Color;
	linear255Color.reserve(_currentLedColors.size());
	for (const ColorRgb& c : _currentLedColors)
	{
		linear255Color.push_back(InfiniteProcessing::srgbNonlinearToLinear(linalg::vec<uint8_t, 3>{ c.red, c.green, c.blue }) / 65535.0f);
	}

	_infinite->setCurrentSmoothingConfigParams(priorityInfo.smooth_cfg);

	updateResult(std::move(linear255Color));
}

void LightingInstance::updateResult(std::vector<linalg::aliases::float3>&& _ledBuffer)
{
	// stats
	int64_t now = InternalClock::now();
	int64_t diff = now - _computeStats.statBegin;
	int64_t prevToken = _computeStats.token;

	if (_computeStats.token <= 0 || diff < 0)
	{
		_computeStats.token = PerformanceCounters::currentToken();
		_computeStats.statBegin = now;
		_computeStats.total = 1;
	}
	else if (prevToken != (_computeStats.token = PerformanceCounters::currentToken()))
	{

		if (diff >= 59000 && diff <= 61000)
			emit GlobalSignals::getInstance()->SignalPerformanceNewReport(
				PerformanceReport(overglow::PerformanceReportType::INSTANCE, _computeStats.token, _name, _computeStats.total / qMax(diff / 1000.0, 1.0), _computeStats.total, 0, 0, getInstanceIndex()));

		_computeStats.statBegin = now;
		_computeStats.total = 1;
	}
	else
		_computeStats.total++;

	if (_hwLedCount > static_cast<int>(_ledBuffer.size()))
	{
		_ledBuffer.resize(_hwLedCount, {0.0f, 0.0f, 0.0f});
	}

	QVector<ColorRgb> nonLinear255Color;
	nonLinear255Color.reserve(_ledBuffer.size());
	for (const linalg::aliases::float3& c : _ledBuffer)
	{
		auto temp = ColorSpaceMath::round_to_0_255<linalg::aliases::byte3>(InfiniteProcessing::srgbLinearToNonlinear(c) * 255.0f);
		nonLinear255Color.push_back(ColorRgb(temp.x, temp.y, temp.z));
	}
	_currentLedColors = std::move(nonLinear255Color);

	if (isSignalConnected(QMetaMethod::fromSignal(&LightingInstance::SignalRawColorsChanged)))
	{
		emit SignalRawColorsChanged(_currentLedColors);
	}

	if (_ledDeviceWrapper->enabled())
	{		
		_infinite->incomingColors(std::move(_ledBuffer));		
	}
}

void LightingInstance::identifyLed(const QJsonObject& params)
{
	_ledDeviceWrapper->handleComponentState(overglow::Components::COMP_LEDDEVICE, true);
	_ledDeviceWrapper->identifyLed(params);
}

QJsonObject LightingInstance::getJsonInfo(bool full)
{
	QJsonObject info;
	uint64_t now = InternalClock::now();
	int currentPriority = getCurrentPriority();

	////////////////////////////////////
	// collect priorities information //
	////////////////////////////////////

	QJsonArray priorities;
	for (const Muxer::InputInfo& priorityInfo : _muxer->getInputInfoTable())
		if (priorityInfo.priority != Muxer::LOWEST_PRIORITY)
		{
			QJsonObject item;
			item["priority"] = priorityInfo.priority;
			if (priorityInfo.timeout > 0)
				item["duration_ms"] = int(priorityInfo.timeout - now);

			// owner has optional informations to the component
			if (!priorityInfo.owner.isEmpty())
				item["owner"] = priorityInfo.owner;

			item["componentId"] = QString(overglow::componentToIdString(priorityInfo.componentId));
			item["origin"] = priorityInfo.origin;
			item["active"] = (priorityInfo.timeout >= -1);
			item["visible"] = (priorityInfo.priority == currentPriority);

			if (priorityInfo.componentId == overglow::COMP_COLOR)
			{
				QJsonObject LEDcolor;

				// add RGB Value to Array
				QJsonArray RGBValue;
				RGBValue.append(priorityInfo.staticColor.red);
				RGBValue.append(priorityInfo.staticColor.green);
				RGBValue.append(priorityInfo.staticColor.blue);
				LEDcolor.insert("RGB", RGBValue);

				uint16_t Hue;
				float Saturation, Luminace;

				// add HSL Value to Array
				QJsonArray HSLValue;
				ColorRgb::rgb2hsl(priorityInfo.staticColor.red,
					priorityInfo.staticColor.green,
					priorityInfo.staticColor.blue,
					Hue, Saturation, Luminace);

				HSLValue.append(Hue);
				HSLValue.append(Saturation);
				HSLValue.append(Luminace);
				LEDcolor.insert("HSL", HSLValue);

				item["value"] = LEDcolor;
			}


			(priorityInfo.priority == currentPriority)
				? priorities.prepend(item)
				: priorities.append(item);
		}

	info["priorities"] = priorities;
	info["priorities_autoselect"] = sourceAutoSelectEnabled();

	if (!full)
		return info;

	////////////////////////////////////
	// collect adjustment information //
	////////////////////////////////////

	QJsonArray adjustmentArray = _infinite->getCurrentProcessingConfig();

	info["adjustment"] = adjustmentArray;

	////////////////////////////////////
	//   collect effect information   //
	////////////////////////////////////

	QJsonArray effects;
	std::list<EffectDefinition> effectsDefinitions = getEffects();

	for (const EffectDefinition& effectDefinition : effectsDefinitions)
	{
		QJsonObject effect;
		effect["name"] = QString::fromStdString(effectDefinition.name);
		effects.append(effect);
	}
	info["effects"] = effects;

	////////////////////////////////////
	//    collect running effects     //
	////////////////////////////////////

	QJsonArray activeEffects;
	std::list<ActiveEffectDefinition> activeEffectDefinitionList = _effectEngine->getActiveEffects();

	for (const ActiveEffectDefinition& activeEffectDefinition : activeEffectDefinitionList)
	{
		if (activeEffectDefinition.priority != Muxer::LOWEST_PRIORITY - 1)
		{
			QJsonObject activeEffect;
			activeEffect["name"] = activeEffectDefinition.name;
			activeEffect["priority"] = activeEffectDefinition.priority;
			activeEffect["timeout"] = activeEffectDefinition.timeout;
			activeEffects.append(activeEffect);
		}
	}
	info["activeEffects"] = activeEffects;

	////////////////////////////////////
	//       collect led colors       //
	////////////////////////////////////

	QJsonArray activeLedColors;
	const Muxer::InputInfo& priorityInfo = _muxer->getInputInfo(_muxer->getCurrentPriority());
	if (priorityInfo.componentId == overglow::COMP_COLOR)
	{
		// check if LED Color not Black (0,0,0)
		if ((priorityInfo.staticColor.red +
			priorityInfo.staticColor.green +
			priorityInfo.staticColor.blue !=
			0))
		{
			QJsonObject LEDcolor;

			// add RGB Value to Array
			QJsonArray RGBValue;
			RGBValue.append(priorityInfo.staticColor.red);
			RGBValue.append(priorityInfo.staticColor.green);
			RGBValue.append(priorityInfo.staticColor.blue);
			LEDcolor.insert("RGB Value", RGBValue);

			uint16_t Hue;
			float Saturation, Luminace;

			// add HSL Value to Array
			QJsonArray HSLValue;
			ColorRgb::rgb2hsl(priorityInfo.staticColor.red,
				priorityInfo.staticColor.green,
				priorityInfo.staticColor.blue,
				Hue, Saturation, Luminace);

			HSLValue.append(Hue);
			HSLValue.append(Saturation);
			HSLValue.append(Luminace);
			LEDcolor.insert("HSL Value", HSLValue);

			activeLedColors.append(LEDcolor);
		}
	}
	info["activeLedColor"] = activeLedColors;

	///////////////////////////////////////
	//    collect vailable components    //
	///////////////////////////////////////

	QJsonArray component;
	for (const auto& comp : _componentController->getComponentsState())
	{
		QJsonObject item;
		item["name"] = QString::fromStdString(overglow::componentToIdString(comp.first));
		item["enabled"] = comp.second;

		component.append(item);
	}
	info["components"] = component;

	////////////////
	//    MISC    //
	////////////////

	info["videomodehdr"] = (_componentController->isComponentEnabled(overglow::Components::COMP_HDR)) ? 1 : 0;
	info["leds"] = getSetting(settings::type::LEDS).array();
	info["imageToLedMappingType"] = ImageToLedManager::mappingTypeToStr(_imageProcessor->getLedMappingType());

	return info;
}

int LightingInstance::hasLedClock()
{
	return _ledDeviceWrapper->hasLedClock();
}

bool LightingInstance::getScanParameters(size_t led, double& hscanBegin, double& hscanEnd, double& vscanBegin, double& vscanEnd) const
{
	return _imageProcessor->getScanParameters(led, hscanBegin, hscanEnd, vscanBegin, vscanEnd);
}


void LightingInstance::turnGrabbers(bool active, bool includingSystemGrabber, bool includingVideoGrabber)
{
	_componentController->turnGrabbers(active, includingSystemGrabber, includingVideoGrabber);
}
