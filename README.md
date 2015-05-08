Sitecore Conditional configs: deploy just 1 package

When deploying Sitecore to your DTAP environment, you will always need a separate set of configuration files per environment.
One solution to accomplish is this, is to use web.config transforms with slowcheetah.
What I personally dislike about this approach, is that you have to create separate builds per environment.

That is why we at eFocus came up with a different approach: conditional configs.
Conditional configs allow you to create separate configs per environment, but allows you to specify which environment this applies to.
The current available filters are machinename and installpath. If you need any other filters, let me know. The current two work for all our environments.
Now you can create a deploy package for you entire sitecore folder, including all configs. The configs will only apply if the match is correct.

The patch system works exactly like you are used to from the sitecore include configs.
You just have to place them in a separate folder named include-conditional.
From version 2.0, you can also place the condition-* attributes on the config files inside sitecore's config folder!

Also, we have built in 2 extra config options: files having the name of the rootnode set to “connectionStrings”, will patch your web.config’s connectionstring section
and files with rootnode “mailSettings” will patch your mailsettings section of the web.config.

You enable the conditional configs by either installing the nuget package Efocus.Sitecore.ConditionalConfig or by downloading the source from https://github.com/efocus-nl/efocus.sitecore.conditionalconfigs and include it in your project.
You have to patch your sitecore’s web.config on only one place, you have to place
```
<section name="sitecore" type="Efocus.Sitecore.ConditionalConfig.ConfigReader, Efocus.Sitecore.ConditionalConfig"></section>  
```
instead of
```
<section name="sitecore" type="Sitecore.Configuration.ConfigReader, Sitecore.Kernel"></section>  
```
Sample of how your condtional configs in app_config/Include-condtional might look:
```
< ?xml version="1.0"?>  
<configuration xmlns:patch="http://www.sitecore.net/xmlconfig/" condition-machinename="E([1-9])([0-9]{3})|EFOCUSDEV1">  
  <sitecore>  
    <!-- HOSTNAMES -->  
    <sc .variable="" name="dtap-hostname-website-corporate" value="localhost"></sc>  
  
    <!-- DATABASE SETTINGS -->  
    <sc .variable="" name="dtap-EnableDebug" value="true"></sc>  
    <sc .variable="" name="dtap-livemode-database" value="master"></sc>  
    <sc .variable="" name="dtap-codefirst" value="true"></sc>  
  </sitecore>  
</configuration>  
```
As you can see on the rootnode, it has a condition “machinename” it’s a regular-expression, matched against the computer name at sitecore startup.
The other option is, like noted above, to match the installpath:
```
< ?xml version="1.0" encoding="utf-8"?>  
<connectionstrings condition-machinename="PRODCMS" condition-rootpath="web\\cms">  
  <connectionstring name="core" value="user id=sa;password=;Data Source=myserver;Database=Sitecore.Core_P"></connectionstring>  
  <connectionstring name="master" value="user id=sa;password=;Data Source=myserver;Database=Sitecore.Master_P"></connectionstring>  
  <connectionstring name="web" value="user id=sa;password=;Data Source=myserver;Database=Sitecore.Web_P"></connectionstring>  
</connectionstrings>  
```
In this case, there are two conditions, they have to match BOTH

Well, that’s all there is to it. If you have an opinion or question, leave a comment
