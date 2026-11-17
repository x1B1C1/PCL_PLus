using PCL.Core.ViewModel.Homepage;

namespace PCL.Pages.PageHomepage;

public partial class PageHomepageNewsView : MyPageRight
{
    public PageHomepageNewsView()
    {
        InitializeComponent();
        DataContext = new NewsViewModel();
    }
}