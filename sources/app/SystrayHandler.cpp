/* SystrayHandler.cpp
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
	#include <QSettings>
	#include <list>
#endif

#include <windows.h>

#include <QBuffer>
#include <QByteArray>
#include <QCoreApplication>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QStringList>
#include <QSettings>

#include <OverglowConfig.h>

#include <image/ColorRgb.h>
#include <effects/EffectDefinition.h>
#include <webserver/WebServer.h>
#include <utils/Logger.h>
#include <systray/Systray.h>
#include <utils-image/utils-image.h>

#include "AppDaemon.h"
#include "SystrayHandler.h"

SystrayHandler::SystrayHandler(AppDaemon* appDaemon, quint16 webPort, QString rootFolder)
	: QObject(),
	_menu(nullptr),
	_haveSystray(false),
	_webPort(webPort),
	_rootFolder(rootFolder),
	_selectedInstance(-1)
{
	Q_INIT_RESOURCE(resources);

	std::shared_ptr<InstanceManager> instanceManager;
	appDaemon->getInstanceManager(instanceManager);
	_instanceManager = instanceManager;
	connect(instanceManager.get(), &InstanceManager::SignalInstanceStateChanged, this, &SystrayHandler::signalInstanceStateChangedHandler);
	connect(instanceManager.get(), &InstanceManager::SignalSettingsChanged, this, &SystrayHandler::signalSettingsChangedHandler);

	_haveSystray = SystrayInitialize(nullptr);
}

SystrayHandler::~SystrayHandler()
{
	printf("Releasing SysTray\n");
	if (_haveSystray)
		SystrayClose();
}

bool SystrayHandler::isInitialized()
{
	return _haveSystray;
}

void SystrayHandler::close()
{
	if (_haveSystray)
	{
		SystrayClose();
	}
}

static QString preloadSvg(const QString& filename)
{
	if (filename.indexOf(":/") == 0)
	{
		QFile stream(filename);
		if (!stream.open(QIODevice::ReadOnly))
			return filename;
		QByteArray ar = stream.readAll();
		stream.close();

		return QString(ar);
	}
	return filename;
}

static void loadSvg(std::unique_ptr<SystrayMenu>& menu, QString filename, [[maybe_unused]] QString rootFolder, [[maybe_unused]] QString destFilename = "")
{
	if (!menu)
		return;

	int iconDim = 16;

	if (filename == ":/tray-icon.svg")
		iconDim = 32;

	std::vector<uint8_t> ar;
	QString svgFile = preloadSvg(filename);
	utils_image::svg2png(svgFile.toStdString(), iconDim, iconDim, ar);

	menu->icon.resize(ar.size());
	memcpy(menu->icon.data(), ar.data(), ar.size());
}

void SystrayHandler::createSystray()
{
	if (!_haveSystray)
		return;

	std::unique_ptr<SystrayMenu> mainMenu = std::make_unique<SystrayMenu>();

	loadSvg(mainMenu, ":/tray-icon.svg", _rootFolder);

	// settings menu
	std::unique_ptr<SystrayMenu> settingsMenu = std::make_unique<SystrayMenu>();
	loadSvg(settingsMenu, ":/settings.svg", _rootFolder);
	settingsMenu->label = "&Settings";
	settingsMenu->context = this;
	settingsMenu->callback = [](SystrayMenu* m) {
		SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
		if (sh != nullptr)
			QUEUE_CALL_0(sh, settings);
	};

	// separator 1
	std::unique_ptr<SystrayMenu> separator1 = std::make_unique<SystrayMenu>();
	separator1->label = "-";

	// instances
	std::unique_ptr<SystrayMenu> instances = std::make_unique<SystrayMenu>();
	instances->label = "Instances";
	loadSvg(instances, ":/instance.svg", _rootFolder);

	std::shared_ptr<InstanceManager> instanceManager = _instanceManager.lock();
	if (instanceManager != nullptr)
	{
		std::vector<QString> instancesList;
		SAFE_CALL_0_RET(instanceManager.get(), getInstances, std::vector<QString>, instancesList);
		for (int i = static_cast<int>(instancesList.size()) - 2; i >= 0; i -= 2)
		{
			bool ok = false;
			int key = instancesList[i].toInt(&ok);
			if (ok)
			{
				std::unique_ptr<SystrayMenu> instanceItem = std::make_unique<SystrayMenu>();
				instanceItem->label = instancesList[i + 1].toStdString();
				instanceItem->checkGroup = key;
				instanceItem->isChecked = (key == _selectedInstance);
				instanceItem->context = this;
				instanceItem->callback = [](SystrayMenu* m) {
					SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
					sh->_selectedInstance = m->checkGroup;
					if (sh != nullptr)
						QUEUE_CALL_0(sh, selectInstance);
				};

				std::swap(instances->submenu, instanceItem->next);
				std::swap(instances->submenu, instanceItem);
			}
		}

		std::unique_ptr<SystrayMenu> separatorInstance = std::make_unique<SystrayMenu>();
		separatorInstance->label = "-";

		std::swap(instances->submenu, separatorInstance->next);
		std::swap(instances->submenu, separatorInstance);

		std::unique_ptr<SystrayMenu> instancesAll = std::make_unique<SystrayMenu>();
		instancesAll->label = "All";
		instancesAll->isChecked = (_selectedInstance == -1);
		instancesAll->context = this;
		instancesAll->callback = [](SystrayMenu* m) {
			SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
			sh->_selectedInstance = -1;
			if (sh != nullptr)
				QUEUE_CALL_0(sh, selectInstance);
		};

		std::swap(instances->submenu, instancesAll->next);
		std::swap(instances->submenu, instancesAll);
	}
	
	// color menu
	std::unique_ptr<SystrayMenu> colorMenu = std::make_unique<SystrayMenu>();
	loadSvg(colorMenu, ":/color.svg", _rootFolder);
	colorMenu->label = "&Color";
	colorMenu->context = this;

	std::list<std::string> colors{ "white", "red", "green", "blue", "yellow", "magenta", "cyan" };
	colors.reverse();
	for (const std::string& color : colors)
	{
		QString svgTemplate = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"32\" height=\"32\" version=\"1.1\" viewBox=\"0 0 32 32\">"
			"<rect width=\"26\" height=\"26\" x=\"3\" y=\"3\" fill=\"%1\" stroke-width=\"2\" stroke=\"gray\" />"
			"</svg>";
		QString svg = QString(svgTemplate).arg(QString::fromStdString(color));
		
		std::unique_ptr<SystrayMenu> colorItem = std::make_unique<SystrayMenu>();
		loadSvg(colorItem, svg, _rootFolder, QString("%1.png").arg(QString::fromStdString(color)));
		colorItem->label = color;
		colorItem->context = this;
		colorItem->callback = [](SystrayMenu* m) {
			SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
			QString colorName = QString::fromStdString(m->label);
			ColorRgb color = utils_image::colorRgbfromString(colorName.toStdString());
			if (sh != nullptr)
				QUEUE_CALL_1(sh, setColor, ColorRgb, color);
		};

		std::swap(colorMenu->submenu, colorItem->next);
		std::swap(colorMenu->submenu, colorItem);
	}

	// effects
	std::list<EffectDefinition> efxs, efxsSorted;
	if (instanceManager != nullptr)
		efxs = instanceManager->getEffects();

	std::unique_ptr<SystrayMenu> effectsMenu = std::make_unique<SystrayMenu>();
	loadSvg(effectsMenu, ":/effects.svg", _rootFolder);
	effectsMenu->label = "&Effects";


	efxs.sort([](const EffectDefinition& a, const EffectDefinition& b) {
		return a.name > b.name;
	});

	std::copy_if(efxs.begin(), efxs.end(),
		std::back_inserter(efxsSorted),
		[](const EffectDefinition& a) { return a.name.find("Music:") != std::string::npos; });

	std::copy_if(efxs.begin(), efxs.end(),
		std::back_inserter(efxsSorted),
		[](const EffectDefinition& a) { return !(a.name.find("Music:") != std::string::npos); });


	for (const EffectDefinition& efx : efxsSorted)
	{
		QString effectName = QString::fromStdString(efx.name);
		std::unique_ptr<SystrayMenu> effectItem = std::make_unique<SystrayMenu>();
		effectItem->label = effectName.toStdString();
		effectItem->context = this;
		effectItem->callback = [](SystrayMenu* m) {
			SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
			QString effectName = QString::fromStdString(m->label);
			if (sh != nullptr)
				QUEUE_CALL_1(sh, setEffect, QString, effectName);
		};

		std::swap(effectsMenu->submenu, effectItem->next);
		std::swap(effectsMenu->submenu, effectItem);
	}

	// clear menu
	std::unique_ptr<SystrayMenu> clearMenu = std::make_unique<SystrayMenu>();
	loadSvg(clearMenu, ":/clear.svg", _rootFolder);
	clearMenu->label = "&Clear";
	clearMenu->context = this;
	clearMenu->callback = [](SystrayMenu* m) {
		SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
		if (sh != nullptr)
			QUEUE_CALL_0(sh, clearEfxColor);
	};

	// separator 2
	std::unique_ptr<SystrayMenu> separator2 = std::make_unique<SystrayMenu>();
	separator2->label = "-";

	// quit menu
	std::unique_ptr<SystrayMenu> quitMenu = std::make_unique<SystrayMenu>();
	loadSvg(quitMenu, ":/quit.svg", _rootFolder);
	quitMenu->label = "&Quit";
	quitMenu->context = this;
	quitMenu->callback = [](SystrayMenu* m) {
		SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
		if (sh != nullptr)
		{
			auto instance = QCoreApplication::instance();
			QUEUE_CALL_0(instance, quit);
		}
	};

	std::unique_ptr<SystrayMenu> separator3 = std::unique_ptr<SystrayMenu>(new SystrayMenu);
	separator3->label = "-";

	std::unique_ptr<SystrayMenu> autostartMenu = std::unique_ptr<SystrayMenu>(new SystrayMenu);
	loadSvg(autostartMenu, ":/autorun.svg", _rootFolder);
	autostartMenu->label = (getCurrentAutorunState()) ? "Disable autostart" : "Enable autostart";
	autostartMenu->context = this;
	autostartMenu->callback = [](SystrayMenu* m) {
		SystrayHandler* sh = qobject_cast<SystrayHandler*>(m->context);
		if (sh != nullptr)
		{
			QUEUE_CALL_0(sh, setAutorunState);
		}
	};

	std::swap(separator2->next, quitMenu);
	std::swap(clearMenu->next, separator2);
	std::swap(effectsMenu->next, clearMenu);
	std::swap(colorMenu->next, effectsMenu);

	std::swap(instances->next, colorMenu);
	std::swap(separator3->next, instances);
	std::swap(autostartMenu->next, separator3);
	std::swap(separator1->next, autostartMenu);

	std::swap(settingsMenu->next, separator1);	
	std::swap(mainMenu->submenu, settingsMenu);

	SystrayUpdate(mainMenu.get());
	std::swap(_menu, mainMenu);
}

bool SystrayHandler::getCurrentAutorunState()
{	
	QSettings reg("HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", QSettings::NativeFormat);
	return reg.value("Overglow", 0).toString() == qApp->applicationFilePath().replace('/', '\\');
}

void SystrayHandler::setAutorunState()
{
	bool currentState = getCurrentAutorunState();
	QSettings reg("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", QSettings::NativeFormat);
	(currentState)
		? reg.remove("Overglow")
		: reg.setValue("Overglow", qApp->applicationFilePath().replace('/', '\\'));
	QUEUE_CALL_0(this, createSystray);
}

void SystrayHandler::selectInstance()
{
	createSystray();
}

void SystrayHandler::setColor(ColorRgb color)
{
	std::shared_ptr<InstanceManager> instanceManager = _instanceManager.lock();
	if (instanceManager)
		QUEUE_CALL_4(instanceManager.get(), setInstanceColor, int, _selectedInstance, int, 1, ColorRgb, color, int, 0);
}


void SystrayHandler::settings()
{
	QString link = QString("http://localhost:%1/").arg(_webPort);

	const wchar_t* array = (const wchar_t*)link.utf16();
	ShellExecuteW(0, 0, array, 0, 0, SW_SHOW);
}

void SystrayHandler::setEffect(QString effect)
{
	std::shared_ptr<InstanceManager> instanceManager = _instanceManager.lock();
	if (instanceManager)
		QUEUE_CALL_3(instanceManager.get(), setInstanceEffect, int, _selectedInstance, QString, effect, int, 1);
}

void SystrayHandler::clearEfxColor()
{
	std::shared_ptr<InstanceManager> instanceManager = _instanceManager.lock();
	if (instanceManager)
		QUEUE_CALL_2(instanceManager.get(), clearInstancePriority, int, _selectedInstance, int, 1);
}

void SystrayHandler::signalInstanceStateChangedHandler(InstanceState state, quint8 instance, const QString& /*name*/)
{
	if (instance == _selectedInstance && state == InstanceState::STOP)
		_selectedInstance = -1;

	createSystray();
}

void SystrayHandler::signalSettingsChangedHandler(settings::type type, const QJsonDocument& data)
{
	if (type == settings::type::WEBSERVER)
	{
		_webPort = data.object()["port"].toInt();
	}
}

