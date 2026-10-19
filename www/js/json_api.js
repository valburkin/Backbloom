function GetAddress()
{
	return window.location.protocol + "//" + window.location.hostname + (window.location.port ? ':' + window.location.port: '')+'/json-rpc?request=';
}

function CopyToClipboard(par)
{
	var textarea = document.createElement("textarea");
	textarea.textContent = par;
	textarea.style.position = "fixed";
	document.body.appendChild(textarea);
	textarea.select();
	try {
		document.execCommand("copy");
	}
	catch (ex) {
		console.warn("Copy to clipboard failed.", ex);		
	}
	finally {
		document.body.removeChild(textarea);
	}
}

function CopyJson(sender)
{	
	CopyToClipboard(sender.text());
}

function CopyLink(sender)
{	
	var rpc = GetAddress();
	var text = sender.text().replace(/[\r\n]/g,'').replace(/\s+/g, '').replace(/\t/g, '');
	var urlenc=encodeURI(rpc+text);
	
	CopyToClipboard(urlenc);
}

function CopyLinkSafe(sender)
{	
	var rpc = GetAddress();
	var text = sender.text().replace(/[\r\n]/g,'').replace(/\t/g, '');
	var urlenc=encodeURI(rpc+text);
	
	CopyToClipboard(urlenc);
}

function RunIt(sender)
{
	var rpc = GetAddress();
	var text = sender.text().replace(/[\r\n]/g,'').replace(/\s+/g, '').replace(/\t/g, '');
	var urlenc=encodeURI(rpc+text);
		
	window.open(urlenc); 
}
function RunItSafe(sender)
{
	var rpc = GetAddress();
	var text = sender.text().replace(/[\r\n]/g,'').replace(/\t/g, '');
	var urlenc=encodeURI(rpc+text);
		
	window.open(urlenc); 
}

$(document).ready( function(){	
$("textarea").attr("placeholder", $.i18n('json_api_ouput'));
performTranslation();
$("#json_api_components_expl_multi").html($.i18n("json_api_components_expl_multi"));

// components
var componentJson = 
`{
	"command":"componentstate",
	"componentstate":
	{
		"component":"{0}",
		"state": {1}
	}
}`;
 
$('input[name="compState"]').change(
	function(){
		if ($("input[name='compState_onoff']:checked").val())
		{
			BuildComponentJson();
		}
	});
$('input[name="compState_onoff"]').change(
	function(){
		if ($("input[name='compState']:checked").val())
		{
			BuildComponentJson();
		}
	});	
	
function BuildComponentJson()
{
	$('button[name="compStateButtons"]').each(function(i, obj) {
		$(this).removeClass('disabled');
	});
	
	var component = $('input[name="compState"]:checked').val();
	var state = $('input[name="compState_onoff"]:checked').val();
	var finJson = componentJson.replace("{0}", component).replace("{1}", state);
	$("#compState_json").text(finJson);
}
////////////////////////////////////////////////////////////////////////////////
var switchJson = 
`{
  "command" : "instance",
  "subcommand" : "switchTo",
  "instance" : {0}
}`;	

$('#instanceIndex').on('input',
	function(){
		var num = $("#instanceIndex").val();
		if ((!isNaN(num)) && !isNaN(parseFloat(num)))
		{
			BuildSwitchJson();
		}
		else
		{
			$('button[name="switchButtons"]').each(function(i, obj) {
				$(this).addClass('disabled');
			});
			$("#switch_json").html("");
		}
	});	
	
function BuildSwitchJson()
{
	$('button[name="switchButtons"]').each(function(i, obj) {
		$(this).removeClass('disabled');
	});
	
	var component = $('#instanceIndex').val();
	var finJson = switchJson.replace("{0}", component);
	$("#switch_json").text(finJson);
}

////////////////////////////////////////////////////////////////////////////////
var serverJson = 
`{
    "command":"serverinfo"
}`

$("#server_json").text(serverJson);

////////////////////////////////////////////////////////////////////////////////
/////////////////////////////////////////////////////////////////////////////////////////////////
var adjustmentJson = 
`{
  "command" : "adjustment",
  "adjustment" : 
  {
     {2}"{0}" : {1}
  }
}`;

const editor_color = createJsonEditor('adjustmentEditor', {
		color              : window.schema.color.properties.channelAdjustment.items
	}, true, true);	
	
function requestAdjustmentJson(type, value)
{	
	var line = '"classic_config" : {0},\n     ';
	const editor = editor_color.getEditor('root.color.classic_config');
	var adj = adjustmentJson;
	
	
	if (type == "classic_config")
		line = '';
	else
		line = line.replace('{0}',  editor.value);
		
	adj = adj.replace("{0}", type).replace("{1}", value).replace("{2}", line);
	$("#adjustment_json").text(adj);
	
	$('button[name="adjustmentButtons"]').each(function(i, obj) {
		$(this).removeClass('disabled');
	});
}

for(var key in window.schema.color.properties.channelAdjustment.items.properties)
{
	const sourceKey = key;
	const sourcePath = 'root.color.'+key;
	const selectEditor = editor_color.getEditor(sourcePath);
	
	if ((selectEditor.path == "root.color.id") || (selectEditor.path == "root.color.leds"))
		selectEditor.container.hidden = true; 
	else
		editor_color.watch(sourcePath,() => {			
			const editor = editor_color.getEditor(sourcePath);
			
			if (editor.format === "colorpicker")
				requestAdjustmentJson(sourceKey, '['+editor.retVal[0]+','+editor.retVal[1]+','+editor.retVal[2]+']');
			else if (editor.getValue() != null)
				requestAdjustmentJson(sourceKey, JSON.stringify(editor.getValue()));		
		});				
}
//////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
var effectJson = 
`{
  "command":"effect",
  "effect":{
    "name":"{0}"
  },
  "duration":{1},
  "priority":{2},
  "origin":"JSON API"
}`;

var colorJson =
`{
  "command":"color",
  "color":{0},
  "duration":{1},
  "priority":{2},
  "origin":"JSON API"
}`;

var rgb = {
	r: 255,
	g: 0,
	b: 0
};

var cpcolor = '#B500FF';

var pickerInit = false;

var duration = 0;

var lastTypeEffect = 0;
var effectPriority = 64;

function sendJsonColor()
{
	lastTypeEffect = 0;
	var colorA = "[" + rgb.r + "," + rgb.g + "," + rgb.b +"]";
	var sc = colorJson.replace("{0}", colorA).replace("{1}",validateDuration(duration)).replace("{2}", effectPriority);
	
	$("#effect_json").text(sc);
	
	$('button[name="effectsButtons"]').each(function(i, obj) {
		$(this).removeClass('disabled');
	});
}

function sendJsonEffect()
{
	lastTypeEffect = 1;
	var efx = $("#effect_select").val();
	if (efx != "__none__")
	{
		var sc = effectJson.replace("{0}", efx).replace("{1}",validateDuration(duration)).replace("{2}", effectPriority);
		
		$("#effect_json").text(sc);
		
		$('button[name="effectsButtons"]').each(function(i, obj) {
			$(this).removeClass('disabled');
		});
	}
}
	
function updateEffectlist()
{
	var newEffects = window.serverInfo.effects;
	
	$('#effect_select').html('<option value="__none__"></option>');
	var soundEffects = [];
	var classicEffects = [];

	for (var i = 0; i < newEffects.length; i++)
	{
		var effectName = newEffects[i].name;
		
		if (effectName.indexOf('Music:') >= 0)
			soundEffects.push(effectName);
		else
			classicEffects.push(effectName);

	}
	$('#effect_select').append(createSel(classicEffects, $.i18n('remote_optgroup_classicEffects')));
	$('#effect_select').append(createSel(soundEffects, $.i18n('remote_optgroup_musicEffects')));			
}

function StepUpDown(me, where)
{
	var target = me.parentElement.parentElement.firstElementChild;
	if (typeof target !== 'undefined' && target.type === "number")
	{
		if (where == "down")
			target.stepDown();
		else
			target.stepUp();
		$(target).trigger('change');
	};
};

$("#effect-stepper-down").off().on("click", function(event) { StepUpDown(this, "down"); DurChange(); });	
$("#effect-stepper-up").off().on("click", function(event) { StepUpDown(this, "up"); DurChange(); });

updateEffectlist();

$("#effect_select").off().on("change", function(event)
{
	sendJsonEffect();
});

if (getStorage('rmcpcolor') != null)
{		
	cpcolor = getStorage('rmcpcolor');
	rgb = hexToRgb(cpcolor);
}

const colorParent = document.querySelector('#remote_color');	
const picker = new Picker(
{
	parent: colorParent,
	editor: true,
	alpha: false,
	color: cpcolor,
	editorFormat: 'rgb',
	popup: 'bottom',
	onChange: function(color)
	{
		if (color != null && color.rgba != null && !isNaN(color.rgba[0]) && !isNaN(color.rgba[1]) && !isNaN(color.rgba[2]))
		{
			var myHex = (color.hex.length >= 7) ? color.hex.slice(0, 7) : color.hex;
			
			$('#remote_color_target').val(color.rgbString);
			document.querySelector('#remote_color_target_invoker').style.backgroundColor = color.rgbaString;
			
			if (!pickerInit)
				pickerInit = true;
			else
			{
				rgb.r = color.rgba[0];
				rgb.g = color.rgba[1];
				rgb.b = color.rgba[2];
				sendJsonColor();			
			}
		}
	}
});

$("#remote_color_target_invoker").off().on("click", function()
{
	picker.openHandler();
});

$("#remote_color_target").off().on("click", function()
{
	picker.openHandler();
});

if (getStorage('rmduration') != null)
{
	$("#remote_duration").val(getStorage('rmduration'));
	duration = getStorage('rmduration');
}

function DurChange(){
	var thisObj = document.getElementById('remote_duration');
	duration = valValue(thisObj.id, thisObj.value, thisObj.min, thisObj.max);
	if (lastTypeEffect == 0)
		sendJsonColor();
	else
		sendJsonEffect();
}

$('#remote_duration').off().on('input', function() {DurChange();} );

$('#effectPriority').off().on('input', function() {
	effectPriority = this.value;
	if (lastTypeEffect == 0)
		sendJsonColor();
	else
		sendJsonEffect();	
});

////////////////////////////////////////////////////////////////////////////////
var clearJson = 
`{
  "command" : "clear",
  "priority" : {0}
}`;	

$('#clearPriority').on('input',
	function(){
		var num = $("#clearPriority").val();
		if ((!isNaN(num)) && !isNaN(parseFloat(num)))
		{
			BuildClearJson();
		}
		else
		{
			$('button[name="clearPriorityButtons"]').each(function(i, obj) {
				$(this).addClass('disabled');
			});
			$("#clearPriority_json").html("");
		}
	});	
	
function BuildClearJson()
{
	$('button[name="clearPriorityButtons"]').each(function(i, obj) {
		$(this).removeClass('disabled');
	});
	
	var component = $('#clearPriority').val();
	var finJson = clearJson.replace("{0}", component);
	$("#clearPriority_json").text(finJson);
}
///////////////////////////////////////////////////////////////////////////////////////////////

////////////////////////////////////////////////////////////////////////////////
///////////////////////////////////////////////////////////////////////////////////////////////

var smoothingControl = 
`{
	"command":"smoothing",
	"subcommand":"{0}",
	"time":{1}
}`;
 
$('input[name="api_smoothing_target"], #json_api_smoothing_time').change(
	function(){
		BuildSmoothingJson();		
	});

$('#json_api_smoothing_time').keyup(
	function(){
		BuildSmoothingJson();
	});
	
function BuildSmoothingJson()
{
	if ($("input[name='api_smoothing_target']:checked").val() && !isNaN($("#json_api_smoothing_time").val()) && $("#json_api_smoothing_time").val() >= 25)
	{
		$('button[name="smoothingControlButtons"]').each(function(i, obj) {
			$(this).removeClass('disabled');
		});
		
		var target = $('input[name="api_smoothing_target"]:checked').val();	
		var time = $('#json_api_smoothing_time').val();
		var finJson = smoothingControl.replace("{0}", target).replace("{1}", time);
	
		$("#smoothingControl_json").text(finJson);
	}
}

/////////////////////////////////////////////////////////////////////////
var currentStateJson = 
`{
  "command" : "current-state",
  "subcommand" : "{0}",
  "instance" : {1}
}`;


function checkCurrentState(){
	var num = $("#instanceCurrentStateIndex").val();
	if ((!isNaN(num)) && !isNaN(parseFloat(num)) && $("input[name='instanceCurrentStateCommand']:checked").val())
	{
		BuildCurrentStatetJson();
	}
	else
	{
		$('button[name="instanceCurrentStateButtons"]').each(function(i, obj) {
			$(this).addClass('disabled');
		});
		$("#instanceCurrentState_json").html("");
	}
};
 
$('#instanceCurrentStateIndex').on('input',
	function(){
		checkCurrentState();
	});
	
$('input[name="instanceCurrentStateCommand"]').change(
	function(){
		checkCurrentState();
	});	
	
function BuildCurrentStatetJson()
{
	$('button[name="instanceCurrentStateButtons"]').each(function(i, obj) {
		$(this).removeClass('disabled');
	});
	
	var component = $("#instanceCurrentStateIndex").val();
	var state = $('input[name="instanceCurrentStateCommand"]:checked').val();
	var finJson = currentStateJson.replace("{1}", component).replace("{0}", state);
	$("#instanceCurrentState_json").text(finJson);
}

});
