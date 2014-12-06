GitBifrost
==========

Built for game developers who are used to the world of versioned game data, GitBifrost bridges the worlds of git and large binaries.

## Getting Started ##

### New Repos ###
####git bifrost init####
Bifrost uses git hooks & filters to transparently handle portions of your repo in an external store. `git-bifrost init` accepts the normal `git init` options and also installs the necessary hooks & filters.

####.gitattributes####
Add any files and extentions you'd like bifrost to track to your `.gitattributes`

    *.ztl filter=bifrost
    *.wav filter=bifrost
    
#### .gitbifrost ####
bifrost has it's own config file which is used to inform which stores will be updated when a push is made to a particular remote. It also provides some rules for helping catch new files that may have been missed in `.gitattributes`.

    [repo]
	    text-size-threshold = 5242880
	    bin-size-threshold = 102400
	    
    [store "project-store.onsite"]
      remote = file:///network_share/project.git
	    url = file:///network_share/project_store
	    
    [store "my-git-store"]
	    remote = https://github.com/user/project.git
	    url = sftp://mydomain.com/project_store

In the `[repo]` secion, the `text-size-threshold` & `bin-size-threshold` limits are used to help catch new files that may be too big. When you commit, bifrost will compare the sizes of the staged files with the threhsolds and abort the commit if any of them are over the limits.

The `[store]` sections are used to map git remote repositories to bifrost store urls. The idea here being that when you `git-push` to a particular remote, the stores that are mapped to that remote are also updated automatically.

Each `[store]` section should have a unique name `[store "store-name"]` which adheres to the git-config format.

#### .gitbifrostuser [incomplete] ####
User specific override file which should be included `.gitignore`. This file is intended to specify usernames and passwords for bifrost stores.

For example `.gitbifrost` may contain `[store "remote-read-only"]` with a username and password for read-only access. With `.gitbifrostuser`, users may override the username and password with their credentials which enable read/write.


### Cloning ###
####git bifrost clone####
Bifrost needs to install some hooks and filters before you do your first checkout so **git-bifrost clone** will clone the repo with --no-checkout, install the hooks and then do the checkout.



## Requirements ##
1. Allow users to version large binaries easily with along side source code
2. Binary handling must be transparent to non-power users and other tools so that anyone and anything that uses git doesn't require knowlege of GitBifrost
3. Allow git power users to work locally in all their normal distributed git glory


### TODO ###
- Add support for compressing binaries
